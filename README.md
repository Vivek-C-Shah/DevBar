<div align="center">
  <img src="design/assets/logo-128.png" width="64" height="64" alt="DevBar logo">
  <h1>DevBar</h1>
  <p><strong>The toolbar that gets out of your way.</strong><br>
  A 125-pixel pill at the top of your screen. Hover for ports, clipboard, containers and Claude sessions, or press a key and ask Jarvis.</p>

  <p>
    <a href="https://github.com/Vivek-C-Shah/DevBar/releases/latest"><img src="https://img.shields.io/badge/Download%20for%20Windows-.exe-2a7ae2?style=for-the-badge&logo=windows" alt="Download for Windows"></a>
    <a href="https://devbar-neon.vercel.app"><img src="https://img.shields.io/badge/devbar-website-555?style=for-the-badge" alt="Website"></a>
  </p>

  <!-- TODO before launch: replace this still with a ~10s GIF at design/assets/demo.gif
       (hover open -> Claude sessions -> kill a port -> collapse). The first screen
       has to move; a still cannot show what hover-expand feels like. -->
  <img src="design/screenshots/02_shelf.png" width="640" alt="DevBar expanded, showing the tab strip and the glass card">
</div>

---

Windows has a taskbar for your apps, notification icons for background noise, and nothing for the handful of things you actually check fifty times a day: *is my Claude Code session done, what's in my clipboard, is anything listening on 5173, did I mean to copy that.*

Mac developers have a hundred menu bar apps for this. Windows developers got nothing. DevBar is that missing thing: a tiny pill docked to the top of your screen, about the size of a browser tab, that expands into a small card when you hover it and disappears the instant you don't need it.

- **Hover, not click.** Glance up, it's there. Look away, it's gone. No window to alt-tab past, no icon to remember, no screen space reserved — every pixel outside the pill is click-through.
- **0.0% CPU while collapsed.** Measured, not asserted. Nothing polls, nothing animates, the blur is switched off at the OS level. It costs you something only in the seconds you are actually looking at it.
- **Nine modules and an SDK for yours.** A module is five methods. No accounts, no trackers, no telemetry — with no keys configured, nothing leaves your machine.

> **Windows only** (10 2004+ or 11, x64), and deliberately so: this is built on real Windows compositor blur and Win32 window behaviour, not a cross-platform shell.

## Install

Download the installer from **[Releases](https://github.com/Vivek-C-Shah/DevBar/releases/latest)** and run it.

- **No admin required** — installs into your own user profile (`%LOCALAPPDATA%\Programs\DevBar`), so it is a plain double-click with no UAC prompt.
- **No separate .NET install** — the runtime is bundled.
- Adds a Start Menu entry, an optional launch-at-sign-in checkbox, and a clean uninstaller.

> The installer is not code-signed yet, so Windows SmartScreen will warn you the first time. **More info → Run anyway**, or build it yourself below.

<details>
<summary><strong>Build it yourself instead</strong></summary>

```powershell
git clone https://github.com/Vivek-C-Shah/DevBar.git
cd DevBar
.\scripts\build-installer.ps1      # publish + package, produces dist\DevBar-Setup-<version>.exe
```

Needs the .NET 8 SDK and Inno Setup; the script tells you if either is missing:

```powershell
winget install Microsoft.DotNet.SDK.8
winget install JRSoftware.InnoSetup
```

**`dotnet` not recognised right after installing the SDK?** winget adds it to your PATH, but a terminal opened *before* the install will not pick that up — open a new one.

To run from source while developing (framework-dependent, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)):

```powershell
dotnet build DevBar.sln -c Release
.\src\DevBar\bin\Release\net8.0-windows10.0.19041.0\DevBar.exe
```

Useful flags while building a module: `--demo clipboard` launches pinned open on a module, `--shelf-seed "C:\a.png;C:\b.txt"` pre-populates the Shelf.

</details>

## The modules

| Module | What it shows |
|---|---|
| 🎙 **Jarvis** | Press a shortcut and talk. It answers out loud and can act on your machine through the other modules' tools, with a spoken confirmation before anything destructive. |
| 🗐 **Clipboard** | Last 25 copies, tagged text/URL/code. Click a chip to re-copy, × to forget one. |
| 🗀 **Shelf** | Drag files in, drag them out somewhere else. Clears on restart, on purpose. |
| 🤖 **Claude Code** | Which sessions are working, idle, or waiting on you. Click one to focus its terminal. |
| 🌐 **Ports** | Every listening TCP port with the process holding it. One click to kill the stuck dev server. |
| 🐳 **Docker** | Containers with start/stop on every row, and honest "not installed" versus "not running" states. |
| 🌿 **Git status** | Dirty or clean, ahead or behind, for the repos you choose to watch. Click to open the folder. |
| ✅ **Build pulse** | A quiet status light per watched build output. Did it build recently, yes or no. |
| 🔊 **Now playing** | Whatever is playing system-wide, with transport controls. |

Docker, Git status and Build pulse are opt-in — see [Config](#config).

### Screenshots

All captured from the app actually running — nothing mocked up.

**Idle** — this is what's on your screen all day:

<img src="design/screenshots/01_idle.png" width="400" alt="DevBar idle, a small pill docked to the top of the screen">

**Claude Code** — session state per project, at a glance:

<img src="design/screenshots/04_claude.png" width="640" alt="DevBar Claude Code module showing three sessions in different states">

**Ports** — real listening ports, one click to kill:

<img src="design/screenshots/05_ports.png" width="640" alt="DevBar Ports module showing listening TCP ports with Port/Process/PID column headers">

<details>
<summary>Clipboard, Shelf, Docker, Git status, Build pulse</summary>

<img src="design/screenshots/03_clipboard.png" width="640" alt="DevBar Clipboard module showing three recent copies">
<img src="design/screenshots/02_shelf.png" width="640" alt="DevBar Shelf module with three dropped files">
<img src="design/screenshots/06_docker.png" width="640" alt="DevBar Docker module showing containers with start buttons">
<img src="design/screenshots/07_git.png" width="640" alt="DevBar Git Status module showing a watched repo on branch master">
<img src="design/screenshots/08_ci.png" width="640" alt="DevBar Build Pulse module showing a recently-built target and a stale one">

</details>

## Using it

- **Hover** the pill to expand. **Move away** and it collapses after a short delay.
- **Tap a tab** at the top of the card to jump to a module, **page** with the arrows at the bottom, or **two-finger swipe** on a precision touchpad. All three do the same thing.
- **Pin** (top-right) holds it open for a session — useful while babysitting a build.
- **Drag a file** over the idle pill and it opens so you can drop onto the Shelf.
- **Right-click the tray icon** for pin, config and exit.

### Config

`%LOCALAPPDATA%\DevBar\config.json`, hand-editable, read on next launch. Module order, disabled modules and clipboard history size all live there with sane defaults. Three fields are opt-in and empty until you fill them in:

```json
{
  "gitWatchedRepos": ["D:\\code\\my-app", "D:\\code\\another-repo"],
  "ciWatchTargets": [
    { "name": "my-app build", "path": "D:\\code\\my-app\\bin\\Release" }
  ],
  "disabledModules": ["media"]
}
```

`ciWatchTargets.path` can be a file or a directory; for a directory, Build pulse watches whichever file inside it changed most recently.

Two more affect what the open card costs you, both explained in [docs/performance.md](docs/performance.md): `"meshDrift": false` stops the glass blobs drifting, and `"meshBlobs": 2` (default 4) draws fewer of them.

### Claude Code: real session state, in one command

```powershell
.\scripts\claude-hooks\install.ps1
```

Without it, the module can only see that a terminal named "claude" exists. With it, Claude Code reports **working**, **waiting on you** and **idle** per project — and "waiting on you" is the whole reason to glance at the bar. Nothing leaves your machine. Details, and the by-hand version, in [scripts/claude-hooks](scripts/claude-hooks).

## Performance

**0.0% CPU collapsed**, ~90–140MB working set. Nothing polls while the bar is shut, including the blur, which is switched off at the OS level.

Expanded costs real CPU, because genuine Windows Acrylic blur-behind is not free. That trade-off, the two config dials that reduce it, and how the idle number stays at zero are written up honestly in **[docs/performance.md](docs/performance.md)**.

## Build your own module

```csharp
public interface IDevBarModule
{
    string Id { get; }
    string DisplayName { get; }
    string IconGlyph { get; }
    UserControl BuildCard();   // your widget, built once and cached
    void OnExpanded();         // start timers here
    void OnCollapsed();        // stop them here - this is the whole performance contract
}
```

That is the entire commitment: no base class, no lifecycle beyond expand and collapse. Drop a compiled DLL into `%LOCALAPPDATA%\DevBar\modules\` and DevBar picks it up on next launch. See **[docs/architecture.md](docs/architecture.md)**, and [CONTRIBUTING.md](CONTRIBUTING.md) if you want to send one back upstream.

## Roadmap

- [x] **v1** — shell, hover-expand, five modules, tray icon, no-admin installer
- [x] **v1.1** — Liquid Glass (Acrylic + mesh gradient), tab-strip nav, Docker, Git status and Build pulse modules, and Jarvis
- [ ] **v1.2** — winget package, per-monitor support, module template repo, code-signed installer
- [ ] **v2** — Slack and PR-radar modules, real CI integration for Build pulse (running/failed, not just idle/success)

## License

MIT — see [LICENSE](LICENSE). Privacy: [PRIVACY.md](PRIVACY.md). Terms: [TERMS.md](TERMS.md).

## How this was built

I directed this one rather than typed it: the code was written by Claude against a design brief I wrote and kept correcting. The corrections are the interesting part — a full-width bar that missed the "small toolbar" intent and had to become a real shell redesign, and a request for genuine glass that surfaced the tension the whole app is now organised around, which is that live compositor blur is expensive and may therefore only exist while you are looking at it. `GlassEffect.cs` and [docs/performance.md](docs/performance.md) are where that decision lives.
