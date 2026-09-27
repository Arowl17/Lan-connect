using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LanConnect.Core;
using LanConnect.Core.Net;
using LanConnect.Windows;
using Microsoft.Win32;

namespace LanConnect.App;

public partial class App : Application
{
    private const string MutexName = @"Local\LanConnect.SingleInstance";
    private const string ShowEventName = @"Local\LanConnect.ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private AppServices? _services;
    private TrayIcon? _tray;
    private MainWindow? _mainWindow;

    internal AppServices Services => _services ?? throw new InvalidOperationException("Not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Already running (probably in the tray): bring its window up instead.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            Shutdown();
            return;
        }

        FileLog.Start();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            _services = AppServices.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show($"Lan Connect could not start:\n\n{ex.Message}\n\nDetails are in the log:\n{FileLog.Path}",
                "Lan Connect", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var settings = Services.Settings.Snapshot();
        if (settings.StartWithWindows != AutoStart.IsEnabled)
        {
            AutoStart.Set(settings.StartWithWindows);
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
            {
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        })
        { IsBackground = true, Name = "LanConnect show-window listener" };
        listener.Start();

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Services.Host.PairingRequested += OnPairingRequested;

        _tray = new TrayIcon(Services, ShowMainWindow, Quit);

        var startHidden = e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (!startHidden || !settings.WelcomeShown || settings.Devices.Count == 0)
        {
            ShowMainWindow();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Log.Info("Display settings changed");
        _services?.Engine.OnDisplaysChanged();
    }

    private void OnPairingRequested(PairingSession session)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var window = new PairingWindow(Services, session, incoming: true);
            window.Show();
            window.Activate();
        });
    }

    internal void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(Services);
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }
        _mainWindow.Activate();
    }

    private async void Quit()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _tray?.Dispose();
        _tray = null;
        if (_services is not null)
        {
            try
            {
                await _services.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Shutdown error", ex);
            }
        }
        Log.Info("Lan Connect stopped");
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI error", e.Exception);
        e.Handled = true; // keep running in the tray; details are in the log
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
