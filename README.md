# Lan Connect

Use one mouse and keyboard across your Windows computers over your home network,
like a software KVM switch. Move the cursor off the edge of one screen and it
appears on the other computer; your keyboard follows it.

Built to fix the problems of tools like Lan Mouse:

- **Gaming-safe.** Game mode stops edge switching while a game is focused (hidden or
  locked cursor, fullscreen apps, GeForce NOW by default) and removes the mouse hook
  entirely, so games get untouched input. The hotkey still works.
- **No accidental switches.** Corners never switch (close buttons, Start, "show desktop"),
  a small push past the edge is needed, and dragging with a button held never switches.
- **No stuck keys.** Every key press and its release always go to the same computer.
  When control moves back, anything still held on the other computer is released.
- **Solid on Wi-Fi.** Mouse movement is sent as running totals, so a lost packet never
  loses movement. Keys and clicks are re-sent until confirmed and land exactly where
  the cursor was. Packets keep flowing every 30 ms while active, which stops Wi-Fi
  power-saving from adding lag spikes.
- **Easy and secure setup.** Computers find each other automatically. Pairing shows the
  same 6-digit code on both screens, like Bluetooth. After that, only paired computers can
  connect, both verify each other, and everything is encrypted (TLS + AES-256-GCM).

## Install

1. Download `LanConnect-Setup-x.y.z.exe` from the latest build
   ([Actions](../../actions) → newest successful run → *Artifacts* → `LanConnect-Setup`)
   or from [Releases](../../releases).
2. Run it on **both** computers. It installs Lan Connect, adds the Windows Firewall
   rule and starts the app. Windows may show a SmartScreen warning because the app
   isn't code-signed yet; click *More info* → *Run anyway*.

There is also a portable `LanConnect.exe` artifact. It needs no install; the app offers
to fix the firewall rule on first start.

## Set up (once)

1. Open Lan Connect on both computers. Its icon lives in the notification area (tray).
2. On either computer, the other one appears under **Nearby computers**. Click **Pair**.
3. Check that both screens show the same 6-digit code and click **The codes match** on both.
4. Pick where the other computer sits: **Left**, **Right**, **Above** or **Below**.
   The other computer learns the matching side automatically.

That's it. Both computers reconnect by themselves from now on, even after restarts.

## Everyday use

| Want to... | Do this |
|---|---|
| Move to the other computer | Push the mouse past the screen edge that faces it |
| Switch without the mouse (works in games) | **Ctrl + Alt + arrow** pointing at the other computer |
| Come back | Push the mouse back across the edge, or **Ctrl + Alt + arrow** pointing home |
| Pause everything | Tray icon → *Pause Lan Connect* |
| Turn game mode off or on | Tray icon → *Game mode* |

Tray icon colours: **blue** ready, **green** controlling or being controlled,
**amber** game mode is holding the edges, **grey** nothing connected or paused.

If the other computer stops responding while you're controlling it, your mouse comes
back automatically within about 1.5 seconds.

## Settings

Left-click the tray icon to open settings:

- **Your computers**: connection status and latency, placement, *Forget*.
- **Switching**: hotkey modifiers, edge resistance (how far to push), dead-corner size.
- **Game mode**: which signals count as "in a game", and a list of apps (`game.exe`) or
  window-title words (`GeForce NOW`) where the edges never switch.
- **General**: this computer's name, start with Windows, visibility, pairing requests.

Settings are stored in `%APPDATA%\LanConnect\settings.json`. Logs are in
`%LOCALAPPDATA%\LanConnect\logs` (*Open log folder* in settings).

## Troubleshooting

- **The other computer doesn't show up under Nearby.** Make sure both are on the same
  network. Some routers isolate Wi-Fi clients ("AP/client isolation", common on guest
  networks), which blocks discovery. Use *Pair by IP address* instead: the other
  computer's IP address is shown in its Windows network settings.
- **Pairing or connecting fails.** The firewall is the usual cause. Settings shows a
  yellow banner with a *Fix it* button if the rule is missing.
- **Occasional lag on Wi-Fi.** In Device Manager, open your Wi-Fi adapter's properties
  and set *Power Saving Mode* / *MIMO Power Save* to maximum performance, and prefer
  5 GHz. A wired connection on at least one computer helps the most.
- **Can't click in Task Manager or admin prompts on the controlled computer.** Windows
  doesn't let normal apps send input to elevated windows. Support for this is planned.
- **A game's anti-cheat complains.** Only the controlled computer receives injected
  input. Play on the computer that has the physical mouse and keyboard.

## Building from source

Requires the .NET 10 SDK.

```sh
dotnet test tests/LanConnect.Core.Tests            # all logic, networking and scenario tests
dotnet publish src/LanConnect.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

Everything compiles on Linux or macOS too (`EnableWindowsTargeting`), and the core
tests run on any OS. The installer is built with Inno Setup from `installer/LanConnect.iss`;
CI (`.github/workflows/build.yml`) does all of this on every push.

### Project layout

| Project | What's in it |
|---|---|
| `src/LanConnect.Core` | Platform-neutral logic: input routing and stuck-key protection, multi-monitor edge geometry, edge and game-mode rules, the switch engine, pairing, TLS control channel, encrypted reliable UDP input stream, discovery, settings |
| `src/LanConnect.Windows` | Windows specifics: low-level hooks, `SendInput`, monitor layout, foreground and cursor inspection, firewall, autostart, DPAPI key storage |
| `src/LanConnect.App` | WPF tray app: settings window, pairing dialog |
| `tests/LanConnect.Core.Tests` | Unit tests plus end-to-end tests with simulated computers over real sockets |

### How it works

- Each computer creates a self-signed ECDSA certificate on first run (stored encrypted
  with DPAPI). Pairing runs a commit/reveal exchange inside TLS and derives the 6-digit
  code from both nonces and both certificate fingerprints. A man-in-the-middle can't
  make both screens show the same code except by a one-in-a-million guess.
- Paired computers keep one mutually authenticated TLS connection (TCP 47810) for
  control messages. Input travels as AES-256-GCM encrypted UDP datagrams (UDP 47810),
  falling back to the TLS connection while UDP isn't confirmed working.
  Discovery uses UDP broadcasts on port 47811.
- The controlling computer swallows its local input with low-level hooks and forwards
  it. The controlled computer injects it with `SendInput`, tracks the cursor itself and
  sends control back when the cursor pushes through the edge facing the controller.

## Roadmap

- Phase 2: clipboard sharing (text, images, then files).
- Phase 3: visual layout editor per monitor, 3+ computers, drag-and-drop files, shared lock screen.
- Later: control of admin prompts and the sign-in screen (Windows service).
