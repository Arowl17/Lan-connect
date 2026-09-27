using LanConnect.Core.Input;

namespace LanConnect.Core.Tests;

public class CaptureRouterTests
{
    private static InputEvent Key(ushort vk, bool down, ushort scan = 0) =>
        InputEvent.KeyEvent(scan == 0 ? (ushort)(vk + 0x100) : scan, vk, extended: false, down);

    [Fact]
    public void KeyHeldBeforeCaptureStaysLocalUntilReleased()
    {
        var router = new CaptureRouter();
        Assert.Equal(InputRoute.Local, router.Route(Key(0x57, true))); // W held while gaming
        router.BeginCapture();

        Assert.Equal(InputRoute.Local, router.Route(Key(0x57, true)));  // auto-repeat
        Assert.Equal(InputRoute.Local, router.Route(Key(0x57, false))); // release reaches local
        Assert.Equal(InputRoute.Remote, router.Route(Key(0x57, true))); // new press goes remote
    }

    [Fact]
    public void EndCaptureReleasesRemoteKeysAndSwallowsLateLocalReleases()
    {
        var router = new CaptureRouter();
        router.BeginCapture();
        Assert.Equal(InputRoute.Remote, router.Route(Key(VirtualKeys.LControl, true)));
        Assert.Equal(InputRoute.Remote, router.Route(Key(0x43, true))); // C

        var releases = router.EndCapture();
        Assert.Equal(2, releases.Count);
        Assert.All(releases, r => Assert.False(r.Down));
        Assert.True(router.HasPendingForeignReleases);

        // Physically still held: repeats and releases must not reach the local computer.
        Assert.Equal(InputRoute.Swallow, router.Route(Key(0x43, true)));
        Assert.Equal(InputRoute.Swallow, router.Route(Key(0x43, false)));
        Assert.Equal(InputRoute.Swallow, router.Route(Key(VirtualKeys.LControl, false)));
        Assert.False(router.HasPendingForeignReleases);

        Assert.Equal(InputRoute.Local, router.Route(Key(0x43, true)));
    }

    [Fact]
    public void SwallowedHotkeyReleaseIsSwallowedToo()
    {
        var router = new CaptureRouter();
        var arrow = Key(VirtualKeys.Right, true);
        router.SwallowUntilReleased(arrow);
        router.BeginCapture();
        Assert.Equal(InputRoute.Swallow, router.Route(Key(VirtualKeys.Right, false)));
        Assert.Equal(InputRoute.Remote, router.Route(Key(VirtualKeys.Right, true)));
    }

    [Fact]
    public void MouseButtonsFollowTheSameRules()
    {
        var router = new CaptureRouter();
        router.BeginCapture();
        Assert.Equal(InputRoute.Remote, router.Route(InputEvent.MouseButtonEvent(MouseButton.Left, true)));
        var releases = router.EndCapture();
        Assert.Contains(releases, r => r.Kind == InputEventKind.MouseButton && r.Button == MouseButton.Left && !r.Down);
        Assert.Equal(InputRoute.Swallow, router.Route(InputEvent.MouseButtonEvent(MouseButton.Left, false)));
    }

    [Fact]
    public void MovementAndWheelFollowCaptureState()
    {
        var router = new CaptureRouter();
        Assert.Equal(InputRoute.Local, router.Route(InputEvent.Move(1, 1)));
        router.BeginCapture();
        Assert.Equal(InputRoute.Remote, router.Route(InputEvent.Move(1, 1)));
        Assert.Equal(InputRoute.Remote, router.Route(InputEvent.Wheel(120, false)));
    }
}

public class HotkeyTests
{
    private static InputEvent Key(ushort vk, bool down) => InputEvent.KeyEvent((ushort)(vk + 0x100), vk, false, down);

    [Fact]
    public void CtrlAltArrowFiresOnceWithDirection()
    {
        var detector = new SwitchHotkeyDetector();
        Assert.Null(detector.Feed(Key(VirtualKeys.LControl, true)));
        Assert.Null(detector.Feed(Key(VirtualKeys.LMenu, true)));
        Assert.Equal(Layout.Side.Right, detector.Feed(Key(VirtualKeys.Right, true)));
        Assert.Null(detector.Feed(Key(VirtualKeys.Right, true))); // auto-repeat
        Assert.Null(detector.Feed(Key(VirtualKeys.Right, false)));
        Assert.Equal(Layout.Side.Left, detector.Feed(Key(VirtualKeys.Left, true)));
    }

    [Fact]
    public void ExtraOrMissingModifiersDoNotFire()
    {
        var detector = new SwitchHotkeyDetector();
        detector.Feed(Key(VirtualKeys.LControl, true));
        Assert.Null(detector.Feed(Key(VirtualKeys.Right, true)));
        detector.Feed(Key(VirtualKeys.Right, false));
        detector.Feed(Key(VirtualKeys.LMenu, true));
        detector.Feed(Key(VirtualKeys.LShift, true));
        Assert.Null(detector.Feed(Key(VirtualKeys.Right, true)));
    }
}

public class CaptureRouterRecoveryTests
{
    private static InputEvent Key(ushort vk, bool down) => InputEvent.KeyEvent((ushort)(vk + 0x100), vk, false, down);

    private static InputEvent Button(MouseButton b, bool down) => InputEvent.MouseButtonEvent(b, down);

    [Fact]
    public void MissedLocalButtonReleaseIsForgottenWhenWindowsSaysItIsUp()
    {
        var router = new CaptureRouter();
        router.Route(Button(MouseButton.Left, true)); // the hook saw the press, then was removed
        Assert.True(router.AnyLocalButtonHeld);

        router.BeginCapture(isDown: _ => false);      // Windows: nothing is actually held
        Assert.False(router.AnyLocalButtonHeld);
        Assert.Equal(InputRoute.Remote, router.Route(Button(MouseButton.Left, true))); // click goes to the laptop
    }

    [Fact]
    public void MissedLocalKeyReleaseIsForgottenAtCapture()
    {
        var router = new CaptureRouter();
        router.Route(Key(0x57, true));                // W pressed, release missed
        router.BeginCapture(isDown: _ => false);
        Assert.Equal(InputRoute.Remote, router.Route(Key(0x57, true))); // typing W now reaches the laptop
    }

    [Fact]
    public void KeysReallyHeldStayLocalAfterPruning()
    {
        var router = new CaptureRouter();
        router.Route(Key(0x57, true));
        router.BeginCapture(isDown: vk => vk == 0x57); // W is genuinely still held
        Assert.Equal(InputRoute.Local, router.Route(Key(0x57, false)));
    }

    [Fact]
    public void SwallowedKeyWithMissedReleaseExpires()
    {
        long now = 1000;
        var router = new CaptureRouter { Clock = () => now };
        router.BeginCapture();
        router.Route(Key(0x42, true));                // B pressed on the laptop
        router.EndCapture();
        Assert.True(router.HasPendingForeignReleases);

        now += 5000;                                  // no auto-repeat for 5 s: B was released unseen
        Assert.Equal(InputRoute.Local, router.Route(Key(0x42, true))); // a fresh press works locally
        Assert.Equal(InputRoute.Local, router.Route(Key(0x42, false)));
    }

    [Fact]
    public void ExpireStaleClearsPendingReleasesSoTheMouseHookCanBeRemoved()
    {
        long now = 0;
        var router = new CaptureRouter { Clock = () => now };
        router.BeginCapture();
        router.Route(Key(0x42, true));
        router.EndCapture();
        now += 2000;
        router.ExpireStale();
        Assert.False(router.HasPendingForeignReleases);
    }

    [Fact]
    public void AutoRepeatKeepsSwallowingAHeldForeignKey()
    {
        long now = 0;
        var router = new CaptureRouter { Clock = () => now };
        router.BeginCapture();
        router.Route(Key(0x42, true));
        router.EndCapture();
        for (var i = 0; i < 50; i++)
        {
            now += 33;                                // ~30 repeats per second
            Assert.Equal(InputRoute.Swallow, router.Route(Key(0x42, true)));
        }
        Assert.Equal(InputRoute.Swallow, router.Route(Key(0x42, false)));
        Assert.False(router.HasPendingForeignReleases);
    }

    [Fact]
    public void SecondPressOfASwallowedButtonIsAFreshPress()
    {
        var router = new CaptureRouter();
        router.BeginCapture();
        router.Route(Button(MouseButton.Right, true));
        router.EndCapture();                          // its release was never seen
        Assert.Equal(InputRoute.Local, router.Route(Button(MouseButton.Right, true)));
        Assert.Equal(InputRoute.Local, router.Route(Button(MouseButton.Right, false)));
    }
}
