using System.Net;
using System.Reflection;
using LanConnect.Core;
using LanConnect.Core.Engine;
using LanConnect.Core.Net;
using LanConnect.Core.Settings;
using LanConnect.Windows;

namespace LanConnect.App;

/// <summary>Everything that runs in the background, created once at startup.</summary>
internal sealed class AppServices : IAsyncDisposable
{
    private AppServices(SettingsStore settings, DeviceIdentity identity, NetworkHost host, DiscoveryService? discovery, WindowsPlatform platform, SwitchEngine engine, HookThread hooks)
    {
        Settings = settings;
        Identity = identity;
        Host = host;
        Discovery = discovery;
        Platform = platform;
        Engine = engine;
        Hooks = hooks;
    }

    public static string Version { get; } =
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public SettingsStore Settings { get; }

    public DeviceIdentity Identity { get; }

    public NetworkHost Host { get; }

    public DiscoveryService? Discovery { get; }

    public WindowsPlatform Platform { get; }

    public SwitchEngine Engine { get; }

    public HookThread Hooks { get; }

    public static AppServices Start()
    {
        var settings = new SettingsStore(AppPaths.SettingsFile);
        var identity = IdentityStore.LoadOrCreate();
        var host = new NetworkHost(identity, () => settings.Snapshot().DeviceName, settings, Version);

        DiscoveryService? discovery = null;
        try
        {
            discovery = new DiscoveryService(identity.Fingerprint, () => settings.Snapshot().DeviceName, host.TcpPort);
            discovery.Start();
        }
        catch (Exception e)
        {
            // Another program holds the discovery port: pairing by IP address still works.
            Log.Error("Network discovery unavailable", e);
            discovery?.Dispose();
            discovery = null;
        }

        var platform = new WindowsPlatform();
        var engine = new SwitchEngine(platform, settings, host, discovery);
        var hooks = new HookThread(engine);
        platform.AttachHooks(hooks);
        host.Start();

        Log.Info($"Lan Connect {Version} started as \"{settings.Snapshot().DeviceName}\" " +
                 $"(id {DeviceIdentity.ShortFingerprint(identity.Fingerprint)}, tcp {host.TcpPort}, udp {host.Udp.Port})");
        if (!hooks.KeyboardHookInstalled)
        {
            Log.Error("Keyboard hook not installed: switching by hotkey will not work");
        }
        return new AppServices(settings, identity, host, discovery, platform, engine, hooks);
    }

    /// <summary>Starts pairing with a computer; the returned session carries the 6-digit code.</summary>
    public Task<PairingSession> PairAsync(IPAddress address, int port) =>
        Host.PairAsync(new IPEndPoint(address, port));

    public async ValueTask DisposeAsync()
    {
        Hooks.Dispose();
        Engine.Dispose();
        Discovery?.Dispose();
        await Host.DisposeAsync();
        Platform.Dispose();
        Identity.Dispose();
    }
}
