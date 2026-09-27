using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using LanConnect.Core.Layout;
using LanConnect.Core.Net;

namespace LanConnect.App;

/// <summary>Shows the pairing code, collects the user's confirmation, then asks where the other computer sits.</summary>
public partial class PairingWindow : Window
{
    private readonly AppServices _services;
    private readonly PairingSession _session;
    private bool _finished;

    internal PairingWindow(AppServices services, PairingSession session, bool incoming)
    {
        _services = services;
        _session = session;
        InitializeComponent();

        CodeTitle.Text = incoming
            ? $"{session.PeerName} wants to pair with this computer"
            : $"Pair with {session.PeerName}";
        CodeHint.Text = $"Check that {session.PeerName} shows exactly the same code, then confirm on both computers.";
        CodeText.Text = PairingCode.Format(session.Code);

        _ = WaitForResultAsync();
    }

    private async Task WaitForResultAsync()
    {
        var paired = await _session.Result;
        _finished = true;
        if (paired)
        {
            ShowPlacement();
        }
        else
        {
            CodePanel.Visibility = Visibility.Collapsed;
            FailedPanel.Visibility = Visibility.Visible;
            FailedText.Text = $"Pairing with {_session.PeerName} was cancelled or timed out. Nothing was changed.";
        }
    }

    private void ShowPlacement()
    {
        CodePanel.Visibility = Visibility.Collapsed;
        PlacementPanel.Visibility = Visibility.Visible;
        PlacementTitle.Text = $"Paired! Where is {_session.PeerName}?";
        Topmost = false;

        // The other computer may already have chosen; show what it picked.
        var current = _services.Settings.Snapshot().Devices.FirstOrDefault(d => d.Id == _session.PeerId)?.Placement;
        if (current is { } side)
        {
            PlacementHint.Text = $"{_session.PeerName} says it is {Describe(side)}. Confirm or pick another side.";
            Highlight(side);
        }
    }

    private void Highlight(Side side)
    {
        var button = side switch
        {
            Side.Left => PlaceLeft,
            Side.Right => PlaceRight,
            Side.Top => PlaceTop,
            _ => PlaceBottom,
        };
        button.Style = (Style)FindResource("PrimaryButton");
    }

    private static string Describe(Side side) => side switch
    {
        Side.Left => "to the left of this computer",
        Side.Right => "to the right of this computer",
        Side.Top => "above this computer",
        _ => "below this computer",
    };

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        _session.Confirm();
        ConfirmButton.IsEnabled = false;
        WaitingText.Text = $"Waiting for {_session.PeerName} to confirm...";
        WaitingText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _session.Reject();
        Close();
    }

    private void OnPlace(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<Side>(tag, out var side))
        {
            _services.Engine.SetPlacement(_session.PeerId, side);
            _services.Settings.Update(s => s.WelcomeShown = true);
        }
        Close();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            _session.Reject();
        }
        base.OnClosing(e);
    }
}
