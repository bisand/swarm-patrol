using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Docker.DotNet;
using Docker.DotNet.Models;
using System.Net.Http.Headers;

public class GhcrPollerWorker : BackgroundService
{
    private readonly ILogger<GhcrPollerWorker> _logger;
    private readonly DockerClient _docker;
    private readonly string _ghcrOwner;
    private readonly string _ghcrUsername;
    private readonly string _ghcrToken;
    private readonly int _interval;

    public GhcrPollerWorker(ILogger<GhcrPollerWorker> logger, DockerClient docker)
    {
        _logger = logger;
        _docker = docker;
        _ghcrOwner = Environment.GetEnvironmentVariable("GHCR_OWNER") ?? throw new("Missing GHCR_OWNER");
        _ghcrUsername = Environment.GetEnvironmentVariable("GHCR_USERNAME") ?? throw new("Missing GHCR_USERNAME");
        _ghcrToken = Environment.GetEnvironmentVariable("GHCR_TOKEN") ?? throw new("Missing GHCR_TOKEN");
        _interval = int.TryParse(Environment.GetEnvironmentVariable("POLL_INTERVAL"), out var i) ? i : 300;

        _logger.LogInformation("Ensure environment variables are set: GHCR_OWNER, GHCR_USERNAME, GHCR_TOKEN, POLL_INTERVAL");
        // Scramble token for logging (show only first 3 and last 3 chars)
        string scrambledToken = _ghcrToken.Length > 6
            ? $"{_ghcrToken.Substring(0, 3)}***{_ghcrToken.Substring(_ghcrToken.Length - 3)}"
            : "***";
        _logger.LogInformation("GHCR Poller initialized with owner: {Owner}, username: {Username}, token: {Token}, interval: {Interval} seconds",
            _ghcrOwner, _ghcrUsername, scrambledToken, _interval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GHCR poller service started");

        // Check if Docker is in Swarm mode
        bool isSwarmMode = await IsSwarmModeAsync();
        _logger.LogInformation("Docker Swarm mode: {IsSwarmMode}", isSwarmMode);

        // Ensure Docker is logged into GHCR
        await EnsureDockerLoginAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (isSwarmMode)
                {
                    await ProcessSwarmServicesAsync(stoppingToken);
                }
                else
                {
                    await ProcessStandaloneContainersAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Polling error");
            }

            await Task.Delay(TimeSpan.FromSeconds(_interval), stoppingToken);
        }

        _logger.LogInformation("GHCR poller service stopping");
    }

    private async Task<bool> IsSwarmModeAsync()
    {
        try
        {
            await _docker.Swarm.InspectSwarmAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task ProcessSwarmServicesAsync(CancellationToken stoppingToken)
    {
        var services = await _docker.Swarm.ListServicesAsync();

        foreach (var service in services)
        {
            var image = service.Spec.TaskTemplate.ContainerSpec.Image;
            if (image is null || !image.Contains($"ghcr.io/{_ghcrOwner}", StringComparison.InvariantCultureIgnoreCase))
                continue;

            _logger.LogInformation("Checking service {ServiceName} with image: {Image}", service.Spec.Name, image);

            var (imageName, tag, currentDigest) = ParseImage(image);
            var latestDigest = await GetLatestDigestFromGHCR(imageName, tag);

            _logger.LogInformation("Service {ServiceName}: Current={CurrentDigest}, Latest={LatestDigest}",
                service.Spec.Name, currentDigest ?? "none", latestDigest);

            // Update if digests are different
            if (currentDigest != latestDigest)
            {
                _logger.LogInformation("Updating service {ServiceName}: {CurrentDigest} -> {LatestDigest}",
                    service.Spec.Name, currentDigest ?? "none", latestDigest);

                await UpdateService(service, imageName, tag, latestDigest, stoppingToken);
            }
            else
            {
                _logger.LogInformation("No update needed for service {ServiceName}", service.Spec.Name);
                var isRunning = await IsDigestRunning(service, latestDigest, stoppingToken);
                if (!isRunning)
                {
                    _logger.LogWarning("Service {ServiceName} is not running the latest digest {LatestDigest}. Please check manually.",
                        service.Spec.Name, latestDigest);
                }
            }
        }
    }

    private async Task ProcessStandaloneContainersAsync(CancellationToken stoppingToken)
    {
        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = false });

        foreach (var container in containers)
        {
            var image = container.Image;
            if (image is null || !image.Contains($"ghcr.io/{_ghcrOwner}", StringComparison.InvariantCultureIgnoreCase))
                continue;

            var (imageName, tag, currentDigest) = ParseImage(image);
            var latestDigest = await GetLatestDigestFromGHCR(imageName, tag);

            if (currentDigest != latestDigest)
            {
                _logger.LogInformation("Container {ContainerName} has updates available. Current: {CurrentDigest}, Latest: {LatestDigest}",
                    container.Names?.FirstOrDefault() ?? container.ID[..12], currentDigest, latestDigest);
                _logger.LogWarning("Automatic container updates not implemented yet. Please manually update container {ContainerName}",
                    container.Names?.FirstOrDefault() ?? container.ID[..12]);
            }
            else
            {
                _logger.LogInformation("No update needed for container {ContainerName}",
                    container.Names?.FirstOrDefault() ?? container.ID[..12]);
            }
        }
    }

    private static (string imageName, string tag, string? digest) ParseImage(string image)
    {
        // Handle digest format: image:tag@sha256:digest
        var parts = image.Split("@sha256:", StringSplitOptions.TrimEntries);
        var imageWithTag = parts[0];
        var digest = parts.Length > 1 ? $"sha256:{parts[1]}" : null;

        // Handle tag format: image:tag
        var tagSeparatorIndex = imageWithTag.LastIndexOf(':');
        if (tagSeparatorIndex == -1)
        {
            return (imageWithTag, "latest", digest);
        }

        var imageName = imageWithTag[..tagSeparatorIndex];
        var tag = imageWithTag[(tagSeparatorIndex + 1)..];

        return (imageName, tag, digest);
    }

    private async Task<string> GetLatestDigestFromGHCR(string image, string tag)
    {
        var parts = image.Replace("ghcr.io/", "").Split("/", 2);
        var owner = parts[0];
        var repo = parts.Length > 1 ? parts[1] : throw new ArgumentException("Invalid image format", nameof(image));

        using var client = new HttpClient();

        try
        {
            // Get bearer token
            var bearerToken = await GetGhcrBearerToken(client, owner, repo);

            // Get manifest digest using HEAD request
            var manifestUrl = $"https://ghcr.io/v2/{owner}/{repo}/manifests/{tag}";
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, manifestUrl));
            resp.EnsureSuccessStatusCode();

            var digest = resp.Headers.GetValues("Docker-Content-Digest").First();
            _logger.LogDebug("Got digest {Digest} for {Image}:{Tag}", digest, image, tag);

            // Validate the digest exists by making another HEAD request to the digest URL
            await ValidateDigestExists(client, owner, repo, digest, bearerToken);

            return digest;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch digest for {Image}:{Tag}", image, tag);
            throw;
        }
    }

    private async Task ValidateDigestExists(HttpClient client, string owner, string repo, string digest, string bearerToken)
    {
        try
        {
            var digestUrl = $"https://ghcr.io/v2/{owner}/{repo}/manifests/{digest}";
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, digestUrl));
            if (!resp.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Digest {digest} does not exist in registry for {owner}/{repo}. Status: {resp.StatusCode}");
            }

            _logger.LogDebug("Validated digest {Digest} exists for {Owner}/{Repo}", digest, owner, repo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate digest {Digest} for {Owner}/{Repo}", digest, owner, repo);
            throw;
        }
    }

    private async Task<string> GetGhcrBearerToken(HttpClient client, string owner, string repo)
    {
        var scope = $"repository:{owner}/{repo}:pull";
        var tokenUrl = $"https://ghcr.io/token?scope={Uri.EscapeDataString(scope)}&service=ghcr.io";

        // Use Basic auth with GitHub PAT
        var basicAuth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{owner}:{_ghcrToken}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        using var tokenResp = await client.GetAsync(tokenUrl);
        tokenResp.EnsureSuccessStatusCode();

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();

        // Simple JSON parsing - find the token value
        var tokenStart = tokenJson.IndexOf("\"token\":\"") + 9;
        var tokenEnd = tokenJson.IndexOf("\"", tokenStart);
        return tokenJson[tokenStart..tokenEnd];
    }

    private async Task EnsureDockerLoginAsync()
    {
        try
        {
            _logger.LogInformation("Logging Docker into GHCR");

            var loginProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "docker",
                    Arguments = $"login ghcr.io -u {_ghcrUsername} --password-stdin",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            loginProcess.Start();
            await loginProcess.StandardInput.WriteLineAsync(_ghcrToken);
            loginProcess.StandardInput.Close();
            await loginProcess.WaitForExitAsync();

            if (loginProcess.ExitCode == 0)
            {
                _logger.LogInformation("Successfully logged Docker into GHCR");
            }
            else
            {
                var error = await loginProcess.StandardError.ReadToEndAsync();
                _logger.LogWarning("Docker login failed: {Error}", error);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to login Docker to GHCR");
        }
    }

    private async Task PullImageAsync(string imageName, string tag, CancellationToken stoppingToken)
    {
        try
        {
            var imageSpec = $"{imageName}:{tag}";
            _logger.LogInformation("Pulling image {ImageSpec} before service update", imageSpec);
            var pullParams = new ImagesCreateParameters { FromImage = imageName, Tag = tag };
            var authConfig = new AuthConfig { ServerAddress = "https://ghcr.io", Username = _ghcrUsername, Password = _ghcrToken };
            await _docker.Images.CreateImageAsync(pullParams, authConfig, new Progress<JSONMessage>(), stoppingToken);
            _logger.LogInformation("Successfully pulled image {ImageSpec}", imageSpec);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pull image {ImageName}:{Tag} before update", imageName, tag);
        }
    }

    private static async Task<bool> PollUntilAsync(Func<Task<bool>> condition, TimeSpan interval, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - start) < timeout && !cancellationToken.IsCancellationRequested)
        {
            if (await condition())
                return true;
            await Task.Delay(interval, cancellationToken);
        }
        return false;
    }

    private async Task UpdateService(SwarmService service, string imageName, string tag, string digest, CancellationToken stoppingToken)
    {
        try
        {
            await PullImageAsync(imageName, tag, stoppingToken);

            _logger.LogInformation("Updating service {ServiceName} to {ImageName}:{Tag}@{Digest}",
                service.Spec.Name, imageName, tag, digest[..16] + "...");

            // Update the service spec with the new digest
            service.Spec.TaskTemplate.ContainerSpec.Image = $"{imageName}:{tag}@{digest}";

            // Add a timestamp label to force update
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            if (service.Spec.TaskTemplate.ContainerSpec.Labels == null)
            {
                service.Spec.TaskTemplate.ContainerSpec.Labels = new Dictionary<string, string>();
            }
            service.Spec.TaskTemplate.ContainerSpec.Labels["swarm-patrol.updated"] = timestamp;

            var parameters = new ServiceUpdateParameters
            {
                Version = (long)service.Version.Index,
                Service = service.Spec
            };

            await _docker.Swarm.UpdateServiceAsync(service.ID, parameters, stoppingToken);
            _logger.LogInformation("Successfully updated service {ServiceName}", service.Spec.Name);

            // Post-update: poll for up to 30 seconds, checking every 2 seconds if digest is running
            bool isRunning = await PollUntilAsync(
                 () => IsDigestRunning(service, digest, stoppingToken),
                 TimeSpan.FromSeconds(2),
                 TimeSpan.FromSeconds(30),
                 stoppingToken
             );
            _logger.LogInformation("Post-update check for service {ServiceName} with digest {Digest}: {IsRunning}", service.Spec.Name, digest, isRunning ? "running" : "not running");

            if (!isRunning)
            {
                _logger.LogWarning("Digest {Digest} is not running after update for service {ServiceName} (timed out after 30s). Falling back to tag only.", digest, service.Spec.Name);
                service.Spec.TaskTemplate.ContainerSpec.Image = $"{imageName}:{tag}";
                var fallbackParameters = new ServiceUpdateParameters
                {
                    Version = (long)service.Version.Index,
                    Service = service.Spec
                };
                await _docker.Swarm.UpdateServiceAsync(service.ID, fallbackParameters, stoppingToken);
                _logger.LogInformation("Fallback to tag-only update for service {ServiceName}", service.Spec.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update service {ServiceName} with digest {Digest}. Will retry without digest.",
                service.Spec.Name, digest);

            // Fallback: try updating with just the tag if digest update fails
            try
            {
                _logger.LogInformation("Attempting fallback update for service {ServiceName} using tag only", service.Spec.Name);

                service.Spec.TaskTemplate.ContainerSpec.Image = $"{imageName}:{tag}";
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                if (service.Spec.TaskTemplate.ContainerSpec.Labels == null)
                {
                    service.Spec.TaskTemplate.ContainerSpec.Labels = new Dictionary<string, string>();
                }
                service.Spec.TaskTemplate.ContainerSpec.Labels["swarm-patrol.fallback"] = timestamp;

                var fallbackParameters = new ServiceUpdateParameters
                {
                    Version = (long)service.Version.Index,
                    Service = service.Spec
                };

                await _docker.Swarm.UpdateServiceAsync(service.ID, fallbackParameters, stoppingToken);
                _logger.LogInformation("Successfully updated service {ServiceName} using fallback method", service.Spec.Name);
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, "Fallback update also failed for service {ServiceName}", service.Spec.Name);
                throw;
            }
        }
    }

    private async Task<bool> IsDigestRunning(SwarmService service, string digest, CancellationToken stoppingToken)
    {
        // Wait a few seconds for tasks to start
        var tasks = await _docker.Tasks.ListAsync(new TasksListParameters { Filters = new Dictionary<string, IDictionary<string, bool>> { ["service"] = new Dictionary<string, bool> { [service.Spec.Name] = true } } });
        return tasks.Any(t => t.Status?.State == TaskState.Running && t.Spec.ContainerSpec?.Image?.Contains(digest) == true);
    }
}
