using System.Text;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;
using LanConnect.Core.Settings;

namespace LanConnect.Core.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lanconnect-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void RoundTripsThroughTheFile()
    {
        var store = new SettingsStore(SettingsPath);
        store.Update(s =>
        {
            s.DeviceName = "Gaming PC";
            s.SwitchHotkey = HotkeyModifiers.Ctrl | HotkeyModifiers.Shift;
            s.Edges.PushDistance = 35;
            s.GameMode.BlockedApps.Add("game.exe");
            s.Devices.Add(new PairedDevice { Id = new string('a', 64), Name = "Laptop", Placement = Side.Right });
        });

        var reloaded = new SettingsStore(SettingsPath).Snapshot();
        Assert.Equal("Gaming PC", reloaded.DeviceName);
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, reloaded.SwitchHotkey);
        Assert.Equal(35, reloaded.Edges.PushDistance);
        Assert.Contains("game.exe", reloaded.GameMode.BlockedApps);
        Assert.Equal(Side.Right, reloaded.Devices.Single().Placement);
        Assert.True(new SettingsStore(SettingsPath).IsTrusted(new string('a', 64)));
    }

    [Fact]
    public void CorruptFileFallsBackToDefaultsAndKeepsACopy()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(SettingsPath, "{ this is not json");
        var store = new SettingsStore(SettingsPath);
        Assert.Empty(store.Snapshot().Devices);
        Assert.True(File.Exists(SettingsPath + ".bad"));
        store.Update(s => s.DeviceName = "fixed");
        Assert.Equal("fixed", new SettingsStore(SettingsPath).Snapshot().DeviceName);
    }

    [Fact]
    public void SnapshotsAreIndependentCopies()
    {
        var store = new SettingsStore(SettingsPath);
        var snapshot = store.Snapshot();
        snapshot.Devices.Add(new PairedDevice { Id = "x", Name = "y" });
        Assert.Empty(store.Snapshot().Devices);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public class DiscoveryParsingTests
{
    private static readonly string ValidId = new('3', 64);

    private static byte[] Beacon(string app = "lan-connect", string? id = null, string name = "Laptop", int port = 47810) =>
        DiscoveryService.EncodeBeacon(new DiscoveryService.Beacon(app, 1, id ?? ValidId, name, port, false));

    [Fact]
    public void AcceptsAValidBeacon()
    {
        Assert.True(DiscoveryService.TryDecode(Beacon(), out var beacon));
        Assert.Equal("Laptop", beacon.Name);
        Assert.Equal(47810, beacon.Port);
    }

    [Theory]
    [InlineData("other-app", null, 47810)]
    [InlineData("lan-connect", "not-a-fingerprint", 47810)]
    [InlineData("lan-connect", null, 0)]
    [InlineData("lan-connect", null, 70000)]
    public void RejectsInvalidBeacons(string app, string? id, int port)
    {
        Assert.False(DiscoveryService.TryDecode(Beacon(app, id, port: port), out _));
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.False(DiscoveryService.TryDecode("garbage"u8, out _));
        Assert.False(DiscoveryService.TryDecode(Encoding.UTF8.GetBytes("{\"App\":1}"), out _));
    }

    [Fact]
    public void NamesAreSanitized()
    {
        Assert.True(DiscoveryService.TryDecode(Beacon(name: "Evil‮gnp.exe\r\n" + new string('x', 200)), out var beacon));
        Assert.DoesNotContain('‮', beacon.Name);
        Assert.DoesNotContain('\n', beacon.Name);
        Assert.True(beacon.Name.Length <= DeviceNames.MaxLength);
        Assert.Equal("Unnamed computer", DeviceNames.Sanitize("  \t "));
    }
}
