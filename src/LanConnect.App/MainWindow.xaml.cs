using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LanConnect.Core;
using LanConnect.Core.Engine;
using LanConnect.Core.Input;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;
using LanConnect.Core.Settings;
using LanConnect.Windows;

namespace LanConnect.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly ObservableCollection<DeviceRow> _devices = new();
    private readonly ObservableCollection<NearbyRow> _nearby = new();
    private readonly DispatcherTimer _refreshTimer;
    private bool _loading;
    private bool _hiddenNoticeShown;

    internal MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        NearbyList.ItemsSource = _nearby;

        LoadSettings();
        RefreshAll();

        _services.Settings.Changed += OnSettingsChanged;
        _services.Engine.StatusChanged += OnEngineStatus;
        _services.Engine.PeersChanged += OnPeersChanged;
        if (_services.Discovery is not null)
        {
            _services.Discovery.DeviceSeen += OnDeviceSeen;
        }

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();

        _ = CheckFirewallAsync();
        _services.Discovery?.AnnounceNow();
    }

    // ------------------------------------------------------------------ loading & refreshing

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = _services.Settings.Snapshot();
            WelcomeCard.Visibility = s.WelcomeShown && s.Devices.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            HotkeyCtrl.IsChecked = s.SwitchHotkey.HasFlag(HotkeyModifiers.Ctrl);
            HotkeyAlt.IsChecked = s.SwitchHotkey.HasFlag(HotkeyModifiers.Alt);
            HotkeyShift.IsChecked = s.SwitchHotkey.HasFlag(HotkeyModifiers.Shift);
            HotkeyWin.IsChecked = s.SwitchHotkey.HasFlag(HotkeyModifiers.Win);
            PushSlider.Value = s.Edges.PushDistance;
            CornerSlider.Value = s.Edges.DeadCornerSize;
            UpdateSliderLabels();
            GameEnabled.IsChecked = s.GameMode.Enabled;
            GameHidden.IsChecked = s.GameMode.BlockWhenCursorHidden;
            GameLocked.IsChecked = s.GameMode.BlockWhenCursorLocked;
            GameFullscreen.IsChecked = s.GameMode.BlockWhenFullscreen;
            GameOptions.IsEnabled = s.GameMode.Enabled;
            GameList.ItemsSource = s.GameMode.BlockedApps.Concat(s.GameMode.BlockedTitles).ToList();
            if (!DeviceName.IsKeyboardFocused)
            {
                DeviceName.Text = s.DeviceName;
            }
            StartWithWindows.IsChecked = s.StartWithWindows;
            VisibleOnNetwork.IsChecked = s.VisibleOnNetwork;
            AllowPairing.IsChecked = s.AllowPairingRequests;
            AboutText.Text = $"Version {AppServices.Version} · This computer's id: {DeviceIdentity.ShortFingerprint(_services.Identity.Fingerprint)}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshAll()
    {
        RefreshStatus();
        RefreshDevices();
        RefreshNearby();
    }

    private void RefreshStatus()
    {
        var status = _services.Engine.Status;
        var connected = _services.Settings.Snapshot().Devices.Where(d => _services.Engine.IsConnected(d.Id)).ToList();
        (string text, Brush brush) = status switch
        {
            { Paused: true } => ("Paused. Enable it again from the tray icon.", (Brush)FindResource("Muted")),
            { State: ControlState.Controlling } => ($"Controlling {status.PeerName}", (Brush)FindResource("Good")),
            { State: ControlState.ControlledBy } => ($"{status.PeerName} is controlling this computer", (Brush)FindResource("Good")),
            _ when connected.Count == 0 => ("No computers connected yet", (Brush)FindResource("Muted")),
            { GameModeReason: { } reason } => ($"Game mode: edge switching paused because {reason}. The hotkey still works.", (Brush)FindResource("Warning")),
            _ => ($"Ready. Connected to {string.Join(", ", connected.Select(d => d.Name))}", (Brush)FindResource("Good")),
        };
        StatusText.Text = text;
        StatusDot.Fill = brush;
    }

    private void RefreshDevices()
    {
        var settings = _services.Settings.Snapshot();
        var paired = settings.Devices;
        var sameShape = paired.Count == _devices.Count
            && paired.Zip(_devices).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name && PlacementIndexOf(p.First.Placement) == p.Second.PlacementIndex);
        if (!sameShape)
        {
            _devices.Clear();
            foreach (var device in paired)
            {
                _devices.Add(new DeviceRow(device.Id, device.Name, PlacementIndexOf(device.Placement)));
            }
        }

        foreach (var row in _devices)
        {
            var device = paired.First(d => d.Id == row.Id);
            var link = _services.Engine.GetLink(row.Id);
            if (link is null)
            {
                row.StatusText = device.LastAddress is null ? "Offline" : $"Offline (last seen at {device.LastAddress})";
                row.StatusBrush = (Brush)FindResource("Muted");
            }
            else
            {
                var latency = link.RttMs >= 0 ? $" · {Math.Max(1, Math.Round(link.RttMs))} ms" : "";
                var path = link.UdpConfirmed ? "" : " · using backup connection";
                var placement = device.Placement is null ? " · choose where it is →" : "";
                row.StatusText = $"Connected{latency}{path}{placement}";
                row.StatusBrush = (Brush)FindResource(device.Placement is null ? "Warning" : "Good");
            }
        }
        NoDevicesText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshNearby()
    {
        var paired = _services.Settings.Snapshot().Devices.Select(d => d.Id).ToHashSet();
        var nearby = (_services.Discovery?.Devices ?? []).Where(d => !paired.Contains(d.Id)).ToList();
        var same = nearby.Count == _nearby.Count && nearby.Zip(_nearby).All(p => p.First.Id == p.Second.Id && p.First.Name == p.Second.Name);
        if (!same)
        {
            _nearby.Clear();
            foreach (var d in nearby)
            {
                _nearby.Add(new NearbyRow(d.Id, d.Name, d.Address, d.TcpPort));
            }
        }
        NoNearbyText.Visibility = _nearby.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_services.Discovery is null)
        {
            NoNearbyText.Text = "Automatic discovery is unavailable (another program uses its port). Pair by IP address below.";
        }
    }

    private static int PlacementIndexOf(Side? side) => side is { } s ? (int)s + 1 : 0;

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(() =>
    {
        LoadSettings();
        RefreshAll();
    });

    private void OnEngineStatus(EngineStatus status) => Dispatcher.BeginInvoke(RefreshStatus);

    private void OnPeersChanged() => Dispatcher.BeginInvoke(RefreshAll);

    private void OnDeviceSeen(DiscoveredDevice device) => Dispatcher.BeginInvoke(RefreshNearby);

    // ------------------------------------------------------------------ devices & pairing

    private void OnPlacementChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { Tag: string id } combo || e.AddedItems.Count == 0)
        {
            return;
        }
        Side? side = combo.SelectedIndex <= 0 ? null : (Side)(combo.SelectedIndex - 1);
        var current = _services.Settings.Snapshot().Devices.FirstOrDefault(d => d.Id == id)?.Placement;
        if (current != side)
        {
            _services.Engine.SetPlacement(id, side);
        }
    }

    private async void OnForget(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }
        var name = _services.Settings.Snapshot().Devices.FirstOrDefault(d => d.Id == id)?.Name ?? "this computer";
        var answer = MessageBox.Show(this, $"Forget {name}? You can pair again any time.", "Lan Connect",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            await _services.Engine.ForgetAsync(id);
        }
    }

    private async void OnPair(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: NearbyRow row } button)
        {
            return;
        }
        button.IsEnabled = false;
        try
        {
            await StartPairingAsync(row.Name, row.IpAddress, row.Port);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnPairManual(object sender, RoutedEventArgs e)
    {
        var text = ManualAddress.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        var port = FramedConnection.DefaultPort;
        var host = text;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var parsedPort) && text.Count(c => c == ':') == 1)
        {
            host = text[..colon];
            port = parsedPort;
        }

        IPAddress? address;
        if (!IPAddress.TryParse(host, out address))
        {
            try
            {
                address = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (SocketException)
            {
                address = null;
            }
        }
        if (address is null)
        {
            MessageBox.Show(this, $"Couldn't find \"{host}\". Check the address shown in Lan Connect on the other computer.",
                "Lan Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await StartPairingAsync(host, address, port);
    }

    private async Task StartPairingAsync(string name, IPAddress address, int port)
    {
        try
        {
            var session = await _services.PairAsync(address, port);
            var window = new PairingWindow(_services, session, incoming: false) { Owner = this };
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Warn($"Pairing with {name} failed: {ex.Message}");
            var reason = ex is InvalidOperationException
                ? "Another pairing is still in progress. Finish or cancel it first."
                : $"Couldn't pair with {name}.\n\nMake sure Lan Connect is open on it, that it allows pairing requests, and that the firewall isn't blocking it.";
            MessageBox.Show(this, reason, "Lan Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ switching

    private void OnHotkeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var mods = HotkeyModifiers.None;
        if (HotkeyCtrl.IsChecked == true) mods |= HotkeyModifiers.Ctrl;
        if (HotkeyAlt.IsChecked == true) mods |= HotkeyModifiers.Alt;
        if (HotkeyShift.IsChecked == true) mods |= HotkeyModifiers.Shift;
        if (HotkeyWin.IsChecked == true) mods |= HotkeyModifiers.Win;
        if (mods == HotkeyModifiers.None)
        {
            MessageBox.Show(this, "Pick at least one key (Ctrl, Alt, Shift or Win) so the arrow keys keep working normally.",
                "Lan Connect", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadSettings();
            return;
        }
        _services.Settings.Update(s => s.SwitchHotkey = mods);
    }

    private void OnPushChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
        if (!_loading && IsLoaded)
        {
            var value = (int)PushSlider.Value;
            _services.Settings.Update(s => s.Edges.PushDistance = value);
        }
    }

    private void OnCornerChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSliderLabels();
        if (!_loading && IsLoaded)
        {
            var value = (int)CornerSlider.Value;
            _services.Settings.Update(s => s.Edges.DeadCornerSize = value);
        }
    }

    private void UpdateSliderLabels()
    {
        if (PushValue is null || CornerValue is null)
        {
            return;
        }
        PushValue.Text = PushSlider.Value == 0 ? "Instant" : $"{PushSlider.Value:0} px push";
        CornerValue.Text = CornerSlider.Value == 0 ? "Off" : $"{CornerSlider.Value:0} px";
    }

    // ------------------------------------------------------------------ game mode

    private void OnGameModeChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var enabled = GameEnabled.IsChecked == true;
        var hidden = GameHidden.IsChecked == true;
        var locked = GameLocked.IsChecked == true;
        var fullscreen = GameFullscreen.IsChecked == true;
        GameOptions.IsEnabled = enabled;
        _services.Settings.Update(s =>
        {
            s.GameMode.Enabled = enabled;
            s.GameMode.BlockWhenCursorHidden = hidden;
            s.GameMode.BlockWhenCursorLocked = locked;
            s.GameMode.BlockWhenFullscreen = fullscreen;
        });
    }

    private void OnAddGame(object sender, RoutedEventArgs e)
    {
        var entry = GameEntry.Text.Trim();
        if (entry.Length == 0)
        {
            return;
        }
        _services.Settings.Update(s =>
        {
            var list = entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s.GameMode.BlockedApps : s.GameMode.BlockedTitles;
            if (!list.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(entry);
            }
        });
        GameEntry.Clear();
    }

    private void OnGameEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnAddGame(sender, e);
        }
    }

    private void OnRemoveGame(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string entry })
        {
            _services.Settings.Update(s =>
            {
                s.GameMode.BlockedApps.RemoveAll(a => a.Equals(entry, StringComparison.OrdinalIgnoreCase));
                s.GameMode.BlockedTitles.RemoveAll(t => t.Equals(entry, StringComparison.OrdinalIgnoreCase));
            });
        }
    }

    // ------------------------------------------------------------------ general

    private void OnDeviceNameCommitted(object sender, RoutedEventArgs e)
    {
        var name = DeviceName.Text.Trim();
        if (name.Length == 0)
        {
            name = Environment.MachineName;
        }
        if (name.Length > 40)
        {
            name = name[..40];
        }
        if (name != _services.Settings.Snapshot().DeviceName)
        {
            _services.Settings.Update(s => s.DeviceName = name);
        }
    }

    private void OnDeviceNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnDeviceNameCommitted(sender, e);
            Keyboard.ClearFocus();
        }
    }

    private void OnGeneralChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        var start = StartWithWindows.IsChecked == true;
        var visible = VisibleOnNetwork.IsChecked == true;
        var allowPairing = AllowPairing.IsChecked == true;
        _services.Settings.Update(s =>
        {
            s.StartWithWindows = start;
            s.VisibleOnNetwork = visible;
            s.AllowPairingRequests = allowPairing;
        });
        AutoStart.Set(start);
    }

    private void OnDismissWelcome(object sender, RoutedEventArgs e)
    {
        _services.Settings.Update(s => s.WelcomeShown = true);
        WelcomeCard.Visibility = Visibility.Collapsed;
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AppPaths.LogDirectory, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Opening log folder failed: {ex.Message}");
        }
    }

    private async Task CheckFirewallAsync()
    {
        var exists = await Task.Run(FirewallHelper.RuleExists);
        FirewallBanner.Visibility = exists ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnFixFirewall(object sender, RoutedEventArgs e)
    {
        var ok = await Task.Run(FirewallHelper.AddRule);
        if (!ok)
        {
            MessageBox.Show(this, "The firewall rule wasn't added (the permission prompt was declined or failed).",
                "Lan Connect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await CheckFirewallAsync();
    }

    // ------------------------------------------------------------------ window

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window keeps Lan Connect running in the tray.
        OnDeviceNameCommitted(this, new RoutedEventArgs());
        e.Cancel = true;
        Hide();
        if (!_hiddenNoticeShown)
        {
            _hiddenNoticeShown = true;
            Log.Info("Settings window hidden; still running in the tray");
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _services.Settings.Changed -= OnSettingsChanged;
        _services.Engine.StatusChanged -= OnEngineStatus;
        _services.Engine.PeersChanged -= OnPeersChanged;
        if (_services.Discovery is not null)
        {
            _services.Discovery.DeviceSeen -= OnDeviceSeen;
        }
        base.OnClosed(e);
    }
}

internal sealed class DeviceRow(string id, string name, int placementIndex) : INotifyPropertyChanged
{
    private string _statusText = "";
    private Brush _statusBrush = Brushes.Gray;

    public static IReadOnlyList<string> Options { get; } =
        ["Not placed yet", "Left of this computer", "Right of this computer", "Above this computer", "Below this computer"];

    public string Id { get; } = id;

    public string Name { get; } = name;

    public int PlacementIndex { get; } = placementIndex;

    public IReadOnlyList<string> PlacementOptions => Options;

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set => Set(ref _statusBrush, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

internal sealed record NearbyRow(string Id, string Name, IPAddress IpAddress, int Port)
{
    public string Address => $"{IpAddress} · id {DeviceIdentity.ShortFingerprint(Id)}";
}
