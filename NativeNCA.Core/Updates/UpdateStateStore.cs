using System.Text.Json;

namespace NativeNCA.Core.Updates;

/// <summary>Update preferences and memory, kept apart from vault.json so they never depend on the vault format.</summary>
public sealed class UpdateState
{
    public bool AutoCheck { get; set; } = true;

    /// <summary>The tray notification was shown for this version: do not show it again.</summary>
    public string? NotifiedVersion { get; set; }

    /// <summary>The user rolled back from this version: do not offer it unprompted.</summary>
    public string? SkippedVersion { get; set; }

    public DateTime? LastCheckUtc { get; set; }
}

/// <summary>Reads and writes <c>%APPDATA%\NativeNCA\update.json</c>.</summary>
public sealed class UpdateStateStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public UpdateStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NativeNCA", "update.json");
    }

    public UpdateState Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_path)) ?? new UpdateState();
            }
            catch
            {
                // A broken file only resets update preferences.
            }
            return new UpdateState();
        }
    }

    public void Update(Action<UpdateState> change)
    {
        lock (_lock)
        {
            var state = Load();
            change(state);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // Not critical: worst case a notification shows again.
            }
        }
    }
}
