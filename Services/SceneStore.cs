using System.Text.Json;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>Scenes persisted to ~/.denonremote/scenes.json.</summary>
public sealed class SceneStore(ILogger<SceneStore> log)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path = AppPaths.Scenes;

    private List<Scene>? _scenes;
    private readonly object _gate = new();

    public event Action? Changed;

    public IReadOnlyList<Scene> All
    {
        get
        {
            lock (_gate)
            {
                _scenes ??= Load();
                return _scenes.ToList();
            }
        }
    }

    public IReadOnlyList<Scene> For(string receiverId) =>
        All.Where(s => s.ReceiverId == receiverId).ToList();

    public void Add(Scene scene)
    {
        lock (_gate)
        {
            _scenes ??= Load();
            _scenes.Add(scene);
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_gate)
        {
            _scenes ??= Load();
            _scenes.RemoveAll(s => s.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    private List<Scene> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            return JsonSerializer.Deserialize<List<Scene>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not read {Path}: {Message}", _path, ex.Message);
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_scenes, Json));
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not write {Path}: {Message}", _path, ex.Message);
        }
    }
}
