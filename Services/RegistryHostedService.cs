namespace DenonRemote.Services;

/// <summary>Connects to every saved receiver as soon as the app starts.</summary>
public sealed class RegistryHostedService(DenonRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.LoadFromStore();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await registry.DisposeAsync();
}
