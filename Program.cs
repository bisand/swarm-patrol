
using System.Diagnostics;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateSlimBuilder(args);
var config = builder.Configuration;
var services = builder.Services;

services.AddHttpClient("ghcr", client =>
{
    client.BaseAddress = new Uri("https://ghcr.io");
    var token = config["GitHub:Token"];
    if (!string.IsNullOrWhiteSpace(token))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
});

services.Configure<AppSettings>(config);
services.AddHostedService<SwarmWatcher>();

var app = builder.Build();
app.Run();

record AppSettings
{
    public required StackEntry[] Stacks { get; init; }
    public int IntervalSeconds { get; init; } = 300;
    public required string GitHubToken { get; init; }
}

record StackEntry(string Name, string ComposeFile);

class SwarmWatcher(ILogger<SwarmWatcher> logger, IHttpClientFactory httpFactory, IOptions<AppSettings> options) : BackgroundService
{
    private readonly AppSettings _settings = options.Value;
    private readonly HttpClient _http = httpFactory.CreateClient("ghcr");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var stack in _settings.Stacks)
            {
                logger.LogInformation("Checking stack {Stack}", stack.Name);
                var images = await GetImagesFromComposeFile(stack.ComposeFile);
                bool needsUpdate = false;

                foreach (var image in images)
                {
                    var runningDigest = await GetRunningDigest(image);
                    var remoteDigest = await GetRemoteDigest(image);
                    logger.LogInformation("Image {Image}: local {Local}, remote {Remote}", image, runningDigest, remoteDigest);

                    if (!string.IsNullOrEmpty(remoteDigest) && runningDigest != remoteDigest)
                    {
                        needsUpdate = true;
                        break;
                    }
                }

                if (needsUpdate)
                {
                    logger.LogWarning("Changes detected. Redeploying {Stack}...", stack.Name);
                    var result = await RunCommand("docker", $"stack deploy -c {stack.ComposeFile} {stack.Name}");
                    logger.LogInformation(result);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.IntervalSeconds), stoppingToken);
        }
    }

    async Task<string[]> GetImagesFromComposeFile(string file)
    {
        var (output, _) = await RunCommand("docker-compose", $"-f {file} config");
        return output
            .Split('\n')
            .Where(line => line.Trim().StartsWith("image:"))
            .Select(line => line.Split("image:")[1].Trim())
            .ToArray();
    }

    async Task<string> GetRunningDigest(string image)
    {
        var (output, _) = await RunCommand("docker", $"image inspect {image} --format='{{{{.Id}}}}'");
        return output.Trim(''', '\n');
    }

    async Task<string?> GetRemoteDigest(string image)
    {
        try
        {
            var (repo, tag) = image.Replace("ghcr.io/", "").Split(':') switch { [var r, var t] => (r, t), _ => (image, "latest") };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v2/{repo}/manifests/{tag}");
            request.Headers.Accept.Add(new("application/vnd.oci.image.manifest.v1+json"));
            var response = await _http.SendAsync(request);
            return response.IsSuccessStatusCode
                ? response.Headers.TryGetValues("Docker-Content-Digest", out var values) ? values.FirstOrDefault() : null
                : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get remote digest for {Image}", image);
            return null;
        }
    }

    static async Task<(string Output, string Error)> RunCommand(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = Process.Start(psi)!;
        var output = await proc.StandardOutput.ReadToEndAsync();
        var error = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (output, error);
    }
}
