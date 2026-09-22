using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>
/// Owns one long-lived <see cref="DenonClient"/> per configured receiver. Singleton, so
/// every browser tab shares the same sockets and the same live state.
/// </summary>
public sealed class DenonRegistry(
    ReceiverStore store,
    DeviceProfileReader profiles,
    GraphicEqClient graphicEq,
    AjaxConfigClient ajax,
    ReceiverInfoReader info,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    private readonly Dictionary<string, DenonClient> _clients = [];
    private readonly object _gate = new();

    /// <summary>Raised when a receiver is added or removed.</summary>
    public event Action? Changed;

    public IReadOnlyList<DenonClient> Clients
    {
        get { lock (_gate) return _clients.Values.OrderBy(c => c.Config.Name).ToList(); }
    }

    public DenonClient? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) return _clients.GetValueOrDefault(id);
    }

    public void LoadFromStore()
    {
        foreach (var config in store.Load())
            AddInternal(config, persist: false);
        Changed?.Invoke();
    }

    public DenonClient Add(ReceiverConfig config)
    {
        var client = AddInternal(config, persist: true);
        Changed?.Invoke();
        return client;
    }

    public void Remove(string id)
    {
        DenonClient? client;
        lock (_gate)
        {
            if (!_clients.Remove(id, out client)) return;
        }

        _ = client!.DisposeAsync();
        Persist();
        Changed?.Invoke();
    }

    public void Rename(string id, string name)
    {
        var client = Find(id);
        if (client is null) return;
        client.Config.Name = name;
        Persist();
        Changed?.Invoke();
    }

    /// <summary>Call after changing a receiver's settings so they survive a restart.</summary>
    public void SaveSettings()
    {
        Persist();
        Changed?.Invoke();
    }

    private DenonClient AddInternal(ReceiverConfig config, bool persist)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            config.Name = string.IsNullOrWhiteSpace(config.Model) ? config.Host : config.Model;

        DenonClient client;
        lock (_gate)
        {
            var existing = _clients.Values.FirstOrDefault(c =>
                string.Equals(c.Config.Host, config.Host, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;

            client = new DenonClient(config, profiles, graphicEq, ajax, info, loggerFactory);
            client.SettingsChanged += Persist;
            _clients[config.Id] = client;
        }

        client.Start();
        if (persist) Persist();
        return client;
    }

    private void Persist() => store.Save(Clients.Select(c => c.Config));

    public async ValueTask DisposeAsync()
    {
        List<DenonClient> clients;
        lock (_gate)
        {
            clients = _clients.Values.ToList();
            _clients.Clear();
        }

        foreach (var client in clients)
            await client.DisposeAsync();
    }
}
