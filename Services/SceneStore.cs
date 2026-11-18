using System.Text.Json;
using DenonRemote.Models;

namespace DenonRemote.Services;

/// <summary>Scenes persisted to ~/.denonremote/scenes.json.</summary>
public sealed class SceneStore(ILogger<SceneStore> log, string? path = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path = path ?? AppPaths.Scenes;

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

    public void Rename(string id, string name) => Change(id, scene => scene.Name = name);

    /// <summary>Replaces what a scene does and keeps its name, id and place.</summary>
    public void Replace(string id, IEnumerable<string> commands) =>
        Change(id, scene => scene.Commands = commands.ToList());

    /// <summary>
    /// Moves a scene one place among its own receiver's scenes. The file holds every
    /// receiver's scenes in one list, so it swaps with the neighbour that belongs to
    /// the same receiver rather than the one that happens to be next to it.
    /// </summary>
    public void Move(string id, int direction)
    {
        lock (_gate)
        {
            _scenes ??= Load();

            var from = _scenes.FindIndex(s => s.Id == id);
            if (from < 0) return;

            var to = from + Math.Sign(direction);
            while (to >= 0 && to < _scenes.Count && _scenes[to].ReceiverId != _scenes[from].ReceiverId)
                to += Math.Sign(direction);
            if (to < 0 || to >= _scenes.Count || to == from) return;

            (_scenes[from], _scenes[to]) = (_scenes[to], _scenes[from]);
            Save();
        }
        Changed?.Invoke();
    }

    private void Change(string id, Action<Scene> change)
    {
        lock (_gate)
        {
            _scenes ??= Load();
            if (_scenes.Find(s => s.Id == id) is not { } scene) return;

            change(scene);
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
