using System.Text.Json;
using System.Text.Json.Serialization;
using LanConnect.Core.Clipboard;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;
using LanConnect.Core.Switching;

namespace LanConnect.Core.Settings;

public sealed class PairedDevice
{
    public required string Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Which side of this computer's screens the device sits on (null = not placed yet).</summary>
    public Side? Placement { get; set; }

    public string? LastAddress { get; set; }

    public int? LastPort { get; set; }

    public DateTime PairedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AppSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;

    public List<PairedDevice> Devices { get; set; } = new();

    /// <summary>Modifiers for the switch hotkey (plus an arrow key towards the other computer).</summary>
    public HotkeyModifiers SwitchHotkey { get; set; } = HotkeyModifiers.Ctrl | HotkeyModifiers.Alt;

    public EdgeOptions Edges { get; set; } = new();

    public GameModeOptions GameMode { get; set; } = new();

    public ClipboardOptions Clipboard { get; set; } = new();

    public bool StartWithWindows { get; set; } = true;

    public bool VisibleOnNetwork { get; set; } = true;

    public bool AllowPairingRequests { get; set; } = true;

    /// <summary>Set once the first-run welcome has been shown.</summary>
    public bool WelcomeShown { get; set; }
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON and doubles as the trust store for pairing.
/// All access goes through this class so it is safe from any thread.
/// </summary>
public sealed class SettingsStore : ITrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();
    private AppSettings _settings;

    public SettingsStore(string path)
    {
        _path = path;
        _settings = Load(path);
    }

    public string FilePath => _path;

    public event Action? Changed;

    /// <summary>A deep copy of the current settings.</summary>
    public AppSettings Snapshot()
    {
        lock (_lock)
        {
            return Clone(_settings);
        }
    }

    public void Update(Action<AppSettings> change)
    {
        lock (_lock)
        {
            var copy = Clone(_settings);
            change(copy);
            _settings = copy;
            Save();
        }
        Changed?.Invoke();
    }

    public bool IsTrusted(string fingerprint)
    {
        lock (_lock)
        {
            return _settings.Devices.Any(d => d.Id == fingerprint);
        }
    }

    public void Trust(string fingerprint, string name) => Update(s =>
    {
        var existing = s.Devices.FirstOrDefault(d => d.Id == fingerprint);
        if (existing is null)
        {
            s.Devices.Add(new PairedDevice { Id = fingerprint, Name = name });
        }
        else
        {
            existing.Name = name;
        }
    });

    private static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.Devices ??= new();
                    loaded.Edges ??= new();
                    loaded.GameMode ??= new();
                    loaded.Clipboard ??= new();
                    loaded.GameMode.BlockedApps ??= GameModeOptions.DefaultBlockedApps();
                    loaded.GameMode.BlockedTitles ??= new();
                    return loaded;
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Settings file unreadable, starting fresh (old copy kept as .bad)", e);
            try
            {
                File.Copy(path, path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }
        }
        return new AppSettings();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_settings, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error("Saving settings failed", e);
        }
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions)!;
}
