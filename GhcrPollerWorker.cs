
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Docker.DotNet; // Add this for DockerClient
using Docker.DotNet.Models;
using System.Net.Http.Headers;
using System.Linq;

public class GhcrPollerWorker : BackgroundService
{
    private readonly ILogger<GhcrPollerWorker> _logger;
    private readonly DockerClient _docker;
    private readonly string _ghcrToken;
    private readonly int _interval;

    public GhcrPollerWorker(ILogger<GhcrPollerWorker> logger, DockerClient docker)
    {
        _logger = logger;
        _docker = docker;
        _ghcrToken = Environment.GetEnvironmentVariable("GHCR_TOKEN") ?? throw new("Missing GHCR_TOKEN");
        _interval = int.TryParse(Environment.GetEnvironmentVariable("POLL_INTERVAL"), out var i) ? i : 300;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GHCR poller service started");

        // Check if Docker is in Swarm mode
        bool isSwarmMode = await IsSwarmModeAsync();
        _logger.LogInformation("Docker Swarm mode: {IsSwarmMode}", isSwarmMode);

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
            if (image is null || !image.Contains("ghcr.io/valueretail", StringComparison.InvariantCultureIgnoreCase)) continue;

            var (imageName, tag, currentDigest) = ParseImage(image);
            var latestDigest = await GetDigestFromGHCR(imageName, tag);

            if (currentDigest != latestDigest)
            {
                _logger.LogInformation("Updating service {ServiceName} to digest {Digest}", service.Spec.Name, latestDigest);
                service.Spec.TaskTemplate.ContainerSpec.Image = $"{imageName}:{tag}@{latestDigest}";
                var parameters = new ServiceUpdateParameters
                {
                    Version = (long)service.Version.Index,
                    Service = service.Spec,
                    RegistryAuthFrom = "spec",
                    RegistryAuth = new AuthConfig
                    {
                        ServerAddress = "https://ghcr.io",
                        IdentityToken = _ghcrToken
                    }
                };
                await _docker.Swarm.UpdateServiceAsync(service.ID, parameters, stoppingToken);
            }
            else
            {
                _logger.LogInformation("No update needed for service {ServiceName}", service.Spec.Name);
            }
        }
    }

    private async Task ProcessStandaloneContainersAsync(CancellationToken stoppingToken)
    {
        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = false });

        foreach (var container in containers)
        {
            var image = container.Image;
            if (image is null || !image.Contains("ghcr.io/valueretail", StringComparison.InvariantCultureIgnoreCase)) continue;

            var (imageName, tag, currentDigest) = ParseImage(image);
            var latestDigest = await GetDigestFromGHCR(imageName, tag);

            if (currentDigest != latestDigest)
            {
                _logger.LogInformation("Container {ContainerName} has updates available. Current: {CurrentDigest}, Latest: {LatestDigest}",
                    container.Names?.FirstOrDefault() ?? container.ID[..12], currentDigest, latestDigest);
                _logger.LogWarning("Automatic container updates not implemented yet. Please manually update container {ContainerName}",
                    container.Names?.FirstOrDefault() ?? container.ID[..12]);
                // TODO: Implement container recreation logic
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
        var parts = image.Split("@sha256:", StringSplitOptions.TrimEntries);
        var imageWithTag = parts[0];
        var digest = parts.Length > 1 ? $"sha256:{parts[1]}" : null;
        
        // Split image name and tag
        var tagSeparatorIndex = imageWithTag.LastIndexOf(':');
        if (tagSeparatorIndex == -1)
        {
            // No tag specified, assume 'latest'
            return (imageWithTag, "latest", digest);
        }
        
        var imageName = imageWithTag[..tagSeparatorIndex];
        var tag = imageWithTag[(tagSeparatorIndex + 1)..];
        
        return (imageName, tag, digest);
    }

    private async Task<string> GetDigestFromGHCR(string image, string tag)
    {
        var parts = image.Replace("ghcr.io/", "").Split("/", 2);
        var owner = parts[0];
        var repo = parts.Length > 1 ? parts[1] : throw new ArgumentException("Invalid image format", nameof(image));
        var scope = $"repository:{owner}/{repo}:pull";

        _logger.LogDebug("Fetching digest for {Image}:{Tag}", image, tag);

        using var client = new HttpClient();

        try
        {
            // Step 1: Get Bearer token from GHCR using GitHub PAT
            string bearerToken;
            if (_ghcrToken.StartsWith("ghp_"))
            {
                bearerToken = await GetGhcrBearerToken(client, scope, owner);
            }
            else
            {
                bearerToken = _ghcrToken; // Assume it's already a bearer token
            }

            // Step 2: Use Bearer token to get manifest
            var manifestUrl = $"https://ghcr.io/v2/{owner}/{repo}/manifests/{tag}";
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var resp = await client.GetAsync(manifestUrl);

            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync();
                _logger.LogError("GHCR manifest request failed. Status: {StatusCode}, Response: {Response}",
                    resp.StatusCode, errorContent);
                resp.EnsureSuccessStatusCode();
            }

            var digest = resp.Headers.GetValues("Docker-Content-Digest").First();
            _logger.LogDebug("Retrieved digest {Digest} for image {Image}:{Tag}", digest, image, tag);
            return digest;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch digest for image {Image}:{Tag} from GHCR", image, tag);
            throw;
        }
    }

    private async Task<string> GetGhcrBearerToken(HttpClient client, string scope, string owner)
    {
        var tokenUrl = $"https://ghcr.io/token?scope={Uri.EscapeDataString(scope)}&service=ghcr.io";

        // Use Basic auth with GitHub PAT to get GHCR bearer token
        var basicAuth = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{owner}:{_ghcrToken}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        _logger.LogDebug("Getting GHCR bearer token for scope: {Scope}", scope);

        using var tokenResp = await client.GetAsync(tokenUrl);
        if (!tokenResp.IsSuccessStatusCode)
        {
            var errorContent = await tokenResp.Content.ReadAsStringAsync();
            _logger.LogError("Failed to get GHCR token. Status: {StatusCode}, Response: {Response}",
                tokenResp.StatusCode, errorContent);
            tokenResp.EnsureSuccessStatusCode();
        }

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();

        // Parse the token from JSON response
        var tokenStart = tokenJson.IndexOf("\"token\":\"") + 9;
        var tokenEnd = tokenJson.IndexOf("\"", tokenStart);
        var token = tokenJson[tokenStart..tokenEnd];

        _logger.LogDebug("Successfully obtained GHCR bearer token");
        return token;
    }
}
