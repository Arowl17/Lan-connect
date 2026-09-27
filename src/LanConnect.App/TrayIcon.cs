using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using LanConnect.Core.Clipboard;
using LanConnect.Core.Engine;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using Application = System.Windows.Application;

namespace LanConnect.App;

/// <summary>The notification-area icon: shows the current state and offers quick actions.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly AppServices _services;
    private readonly Action _showSettings;
    private readonly Action _quit;
    private readonly NotifyIcon _icon;
    private readonly Icon _ready = LoadIcon("tray-ready.ico");
    private readonly Icon _active = LoadIcon("tray-active.ico");
    private readonly Icon _game = LoadIcon("tray-game.ico");
    private readonly Icon _idle = LoadIcon("tray-idle.ico");

    public TrayIcon(AppServices services, Action showSettings, Action quit)
    {
        _services = services;
        _showSettings = showSettings;
        _quit = quit;
        _icon = new NotifyIcon
        {
            Icon = _idle,
            Text = "Lan Connect",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _showSettings();
            }
        };

        services.Engine.StatusChanged += _ => Refresh();
        services.Engine.PeersChanged += Refresh;
        services.ClipboardSync.TransferEvent += OnClipboardTransfer;
        Refresh();
    }

    /// <summary>Only file copies and big transfers get a notification; text just works silently.</summary>
    private void OnClipboardTransfer(ClipboardTransferEvent e) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        var (title, text, icon) = e.Kind switch
        {
            ClipboardTransferKind.Receiving => ("Receiving clipboard", $"Copying {Size(e.TotalBytes)} from {e.PeerName}…", ToolTipIcon.Info),
            ClipboardTransferKind.Received when e.FileCount > 0 => ("Ready to paste", $"{Plural(e.FileCount, "file")} from {e.PeerName} can be pasted now.", ToolTipIcon.Info),
            ClipboardTransferKind.Received => ("Ready to paste", $"The clipboard from {e.PeerName} can be pasted now.", ToolTipIcon.Info),
            ClipboardTransferKind.TooLarge => ("Files not shared", $"The copied files are over the {e.Detail} limit, so they weren't sent to {e.PeerName}. You can raise it in Settings.", ToolTipIcon.Warning),
            _ => ("Clipboard not shared", $"Couldn't share the clipboard with {e.PeerName}: {e.Detail}", ToolTipIcon.Warning),
        };
        _icon.ShowBalloonTip(4000, title, text, icon);
    });

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
        : $"{Math.Max(1, bytes / (1024.0 * 1024)):0} MB";

    private void Refresh() => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        var (icon, text) = Describe();
        _icon.Icon = icon;
        _icon.Text = text.Length > 120 ? text[..120] : text;
    });

    private (Icon Icon, string Text) Describe()
    {
        var status = _services.Engine.Status;
        var connected = ConnectedDevices();
        if (status.Paused)
        {
            return (_idle, "Lan Connect: paused");
        }
        switch (status.State)
        {
            case ControlState.Controlling:
                return (_active, $"Lan Connect: controlling {status.PeerName}");
            case ControlState.ControlledBy:
                return (_active, $"Lan Connect: {status.PeerName} is in control");
        }
        if (connected.Count == 0)
        {
            return (_idle, "Lan Connect: no computers connected");
        }
        if (connected.All(d => d.Placement is null))
        {
            return (_idle, $"Lan Connect: open Settings and choose where {connected[0].Name} is");
        }
        if (status.GameModeReason is not null)
        {
            return (_game, $"Lan Connect: game mode ({status.GameModeReason}). Use {HotkeyText()} to switch");
        }
        return (_ready, $"Lan Connect: ready ({string.Join(", ", connected.Select(d => d.Name))})");
    }

    private List<Core.Settings.PairedDevice> ConnectedDevices() =>
        _services.Settings.Snapshot().Devices.Where(d => _services.Engine.IsConnected(d.Id)).ToList();

    private string HotkeyText()
    {
        var mods = _services.Settings.Snapshot().SwitchHotkey;
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add("Arrow");
        return string.Join("+", parts);
    }

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        var (_, text) = Describe();
        menu.Items.Add(new ToolStripMenuItem(text.Replace("Lan Connect: ", "")) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var status = _services.Engine.Status;
        var settings = _services.Settings.Snapshot();
        if (status.State == ControlState.Controlling)
        {
            menu.Items.Add("Back to this computer", null, (_, _) => _services.Engine.ReturnHome());
        }
        foreach (var device in settings.Devices.Where(d => d.Placement is not null && _services.Engine.IsConnected(d.Id)))
        {
            var arrow = device.Placement switch
            {
                Side.Left => "←",
                Side.Right => "→",
                Side.Top => "↑",
                _ => "↓",
            };
            var item = new ToolStripMenuItem($"Switch to {device.Name}  ({HotkeyText().Replace("Arrow", arrow)})");
            var id = device.Id;
            item.Click += (_, _) => _services.Engine.SwitchTo(id);
            menu.Items.Add(item);
        }

        var pause = new ToolStripMenuItem("Pause Lan Connect") { Checked = status.Paused };
        pause.Click += (_, _) => _services.Engine.Paused = !_services.Engine.Paused;
        menu.Items.Add(pause);

        var gameMode = new ToolStripMenuItem("Game mode (no edge switching in games)") { Checked = settings.GameMode.Enabled };
        gameMode.Click += (_, _) => _services.Settings.Update(s => s.GameMode.Enabled = !s.GameMode.Enabled);
        menu.Items.Add(gameMode);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => _showSettings());
        menu.Items.Add("Quit Lan Connect", null, (_, _) => _quit());
    }

    private static Icon LoadIcon(string name)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"))
            ?? throw new FileNotFoundException(name);
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ready.Dispose();
        _active.Dispose();
        _game.Dispose();
        _idle.Dispose();
    }
}
