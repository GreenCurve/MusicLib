using System.Text.Json;

namespace MusicLib.Core;

/// <summary>Per-device settings. Lives in the app's LOCAL data folder and is never synced.</summary>
public sealed class DeviceSettings
{
    private string _path = "";

    /// <summary>Identifies this install; names its state file in the synced folder.</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;

    /// <summary>The library home: the one folder shared through Syncthing.</summary>
    public string? HomePath { get; set; }

    public static DeviceSettings Load(string path)
    {
        var settings = new DeviceSettings();
        if (File.Exists(path))
        {
            try { settings = JsonSerializer.Deserialize<DeviceSettings>(File.ReadAllText(path)) ?? settings; }
            catch (JsonException) { }
        }
        settings._path = path;
        settings.Save(); // makes sure a fresh DeviceId is persisted
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
