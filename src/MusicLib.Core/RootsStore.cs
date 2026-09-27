using System.Text.Json;

namespace MusicLib.Core;

/// <summary>The list of folders to scan on this device, kept in a small JSON file.</summary>
public sealed class RootsStore
{
    private readonly string _path;

    public RootsStore(string path) => _path = path;

    public List<string> Load()
    {
        if (!File.Exists(_path)) return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path)) ?? new List<string>();
    }

    public bool Add(string root)
    {
        root = Normalize(root);
        var roots = Load();
        if (roots.Contains(root)) return false;
        roots.Add(root);
        Save(roots);
        return true;
    }

    public bool Remove(string root)
    {
        var roots = Load();
        var removed = roots.Remove(Normalize(root));
        if (removed) Save(roots);
        return removed;
    }

    public static string Normalize(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private void Save(List<string> roots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(roots));
    }
}
