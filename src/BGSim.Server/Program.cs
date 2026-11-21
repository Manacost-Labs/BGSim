using BGSim.Server;

if (args is ["--healthcheck"])
{
    return await HealthCheck.RunAsync(TimeSpan.FromSeconds(3));
}

await using var app = ServerApp.Build(args);
await app.RunAsync();
return 0;
