// FILE: Program.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using DotNetEnv;

// Load environment variables from .env file
Env.Load();

Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHostedService<GhcrPollerWorker>();
        services.AddSingleton<Docker.DotNet.DockerClient>(_ =>
            new Docker.DotNet.DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient());
    })
    .Build()
    .Run();
