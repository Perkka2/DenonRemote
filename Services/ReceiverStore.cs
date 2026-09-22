using System.Text.Json;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>Persists the receiver list to ~/.denonremote/receivers.json.</summary>
public sealed class ReceiverStore(ILogger<ReceiverStore> log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path = AppPaths.Receivers;

    public List<ReceiverConfig> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<ReceiverConfig>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not read {Path}: {Message}", _path, ex.Message);
            return [];
        }
    }

    public void Save(IEnumerable<ReceiverConfig> receivers)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(receivers, Json));
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not write {Path}: {Message}", _path, ex.Message);
        }
    }
}
