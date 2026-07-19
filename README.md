<div align="center">
  <img src="design/assets/logo-128.png" width="64" height="64" alt="DevBar logo">
  <h1>DevBar</h1>
  <p><strong>A small, hover‑expand command strip for Windows developers.</strong><br>
  Nearly invisible until you need it. Not another window to manage — a strip that shows up, does one thing, and gets out of the way.</p>
</div>

---

Windows has a taskbar for your apps, notification icons for background noise, and nothing for the handful of things you actually check fifty times a day: *is my Claude Code session done, what's in my clipboard, is anything listening on 5173, did I mean to copy that.*

DevBar is that. It's a tiny pill docked to the top of your screen — about the size of a browser tab — that expands into a small card when you hover over it, and disappears the instant you don't need it.

```
      ▁▁▁▁▁▁▁▁▁▁▁▁▁          ← idle, all day
   ┌─────────────────────┐
   │  Clipboard   ● ○ ○ ○ │  ← hover, 180ms
   │  [chip] [chip] [chip]│
   └─────────────────────┘
```

## Why this and not [taskbar utility / Rainmeter / PowerToys]

- **It's small.** DevBar is not a second taskbar. It's ~125px idle, ~640px expanded, centered near the top edge. It never reserves screen real estate, never pushes your windows around, and every pixel outside the pill is click‑through — whatever's under it still works normally.
- **Hover, not click.** Glance up, it's there. Look away, it's gone. No window to alt‑tab past, no icon to remember.
- **Modular.** Each capability is a self‑contained module behind a five‑method interface. The bar ships with five; writing a sixth takes an afternoon.
- **Genuinely lightweight.** Every module is event‑driven or polls only while its card is on screen — nothing runs while the bar is collapsed. Measured on this machine: **0.0% CPU at idle**, ~90–140MB working set (WPF + the Windows Runtime projections the Media/accent‑color modules use). See [Performance](#performance) below for how that was measured, not just claimed.

## Modules

| Module | What it shows |
|---|---|
| 🗐 **Clipboard** | Last 25 copies, type‑tagged (text/URL/code), click a chip to re‑copy it |
| 🗀 **Shelf** | Ephemeral drag‑and‑drop scratch space for files — clears on restart, on purpose |
| 🤖 **Claude Code** | Active CLI sessions (working / waiting on you / idle), click to focus the terminal |
| 🌐 **Ports** | Listening TCP ports with owning process, one click to kill |
| 🔊 **Now Playing** | Whatever's playing system‑wide (Spotify, a browser tab, anything), with transport controls |

### Screenshots

All captured from the app actually running — nothing mocked up.

**Idle** — this is what's on your screen all day:

<img src="design/screenshots/01_idle.png" width="400" alt="DevBar idle state, a small pill docked to the top of the screen">

**Shelf** — drag files in, they show up as chips, drag them back out anywhere:

<img src="design/screenshots/02_shelf.png" width="640" alt="DevBar Shelf module showing three dropped files">

**Clipboard** — every copy shows up live, type‑tagged, click to re‑copy:

<img src="design/screenshots/03_clipboard.png" width="640" alt="DevBar Clipboard module showing three recent copies">

**Claude Code** — session state per project, at a glance:

<img src="design/screenshots/04_claude.png" width="640" alt="DevBar Claude Code module showing three sessions in different states">

**Ports** — real listening ports on this machine, one click to kill:

<img src="design/screenshots/05_ports.png" width="640" alt="DevBar Ports module showing listening TCP ports">

## Install

**Requirements:** Windows 10 2004+ or Windows 11, [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (the app will prompt to install it if missing).

```powershell
git clone https://github.com/<your-org>/devbar.git
cd devbar
dotnet build DevBar.sln -c Release
```

Run it:

```powershell
.\src\DevBar\bin\Release\net8.0-windows10.0.19041.0\DevBar.exe
```

It has no installer yet — that's a `v1.1` item (MSIX + winget). For now, drop a shortcut to the exe in `shell:startup` if you want it launching with Windows.

### Debug / demo flags

Useful if you're developing a module and want to skip the hover dance:

```powershell
DevBar.exe --demo clipboard          # launch pinned open on a specific module
DevBar.exe --shelf-seed "C:\a.png;C:\b.txt"   # pre-populate the Shelf
```

## Using it

- **Hover** the pill to expand it. **Move away** and it collapses after a short delay.
- **Arrows / two‑finger swipe** page between modules. **Dots** show your position.
- **Pin** (top‑right) holds the bar open for a session — useful while babysitting a build.
- **Drag a file** over the idle pill and it reveals itself so you can drop onto the Shelf.
- **Right‑click the tray icon** for pin/config/exit.

Config lives at `%LOCALAPPDATA%\DevBar\config.json` — module order, disabled modules, clipboard history size, etc. Hand‑editable, reloaded on next launch.

### Claude Code integration

The Claude Code module has two detection tiers. If you want reliable state (not just "a terminal titled claude exists"), have your session write a tiny status file:

```
%LOCALAPPDATA%\DevBar\claude-sessions\<anything>.json
```

```json
{ "project": "my-app", "pid": 1234, "state": "waiting" }
```

`state` is one of `working` / `waiting` / `idle`. No file present → DevBar falls back to scanning for terminal windows with "claude" in the title, best‑effort.

## Performance

This was the actual design constraint, not a footnote — the plan going in was explicit: *don't build a tool that costs more attention than it saves.* So the numbers below are measured on this dev machine, not asserted:

| | Idle | Expanded (Ports module, live) |
|---|---|---|
| CPU | **0.0%** over a 4s sample (`Get-Process` `TotalProcessorTime` delta) | brief scan spikes, then idle again |
| Working set | ~90–140MB | same ballpark |
| Threads | ~30 | ~30 |

How it stays there:
- **Nothing polls while collapsed.** Every module's timer starts in `OnExpanded()` and stops in `OnCollapsed()` — verified by design, not just by convention (see `IDevBarModule`).
- **Clipboard and hover are 100% event‑driven** — `AddClipboardFormatListener` and native mouse‑enter/leave, no polling loop anywhere in the shell.
- **Ports and Claude Code session scans run off the UI thread** (`Task.Run`), so a slow `Process.GetProcesses()` on a loaded dev box never stalls the animation.
- **No AppBar space reservation.** Early builds used the Win32 AppBar API (same mechanism as the taskbar) to reserve the full screen width — it worked, but it's the wrong shape for a tool this small, and it meant every app on the machine had to respect a strip it didn't need to. Current build is a plain topmost window sized to its own content, with `WM_NCHITTEST` making the space around the pill click‑through.
- The working‑set floor (~90MB) is WPF + CLR baseline plus the Windows Runtime projections the accent‑color and Media modules touch once at startup — the honest cost of native UI on .NET, not something the bar wastes ongoing.

Electron would have made the first screen faster to build and the process afterward heavier by 100+MB and non‑zero at idle — that trade is why this is WPF.

## Architecture

```
DevBar.Sdk          IDevBarModule contract — five methods, that's the whole surface
DevBar (app)
  Core/              AppBar-free window shell, native interop, config, clipboard hook
  Modules/*/         one folder per built-in module (Clipboard, Shelf, Claude, Ports, Media)
  Themes/            design tokens (colors, type, radii) — see .tastemaker/style-lock.md
```

A module is a class implementing:

```csharp
public interface IDevBarModule
{
    string Id { get; }
    string DisplayName { get; }
    string IconGlyph { get; }
    UserControl BuildCard();   // your widget, built once and cached
    void OnExpanded();         // start timers here
    void OnCollapsed();        // stop them here — this is the whole performance contract
}
```

Drop a compiled DLL implementing it into `%LOCALAPPDATA%\DevBar\modules\` and DevBar picks it up on next launch — no core recompile needed. A proper module template repo is a `v1.2` item; for now, any of the five built‑in modules under `src/DevBar/Modules/` is the reference implementation to copy from.

## Roadmap

- [x] **v1** — shell, hover‑expand, five modules, tray icon
- [ ] **v1.1** — winget package, MSIX installer, per‑monitor support
- [ ] **v1.2** — module template repo + "build your first module" guide
- [ ] **v2** — Slack, CI‑pulse, PR‑radar modules (OAuth‑backed, once the plugin path is proven)

## Contributing

Issues and PRs welcome. If you're building a module, the contract in `DevBar.Sdk` is intentionally the entire commitment — no base classes to inherit, no lifecycle beyond expand/collapse. Keep new modules to that same standard: **nothing runs while collapsed.**

## License

MIT — see [LICENSE](LICENSE).

## A note on how this was built

DevBar was built end-to-end by Claude (Anthropic), working from a design brief, with a human in the loop for direction and course-correction along the way — including catching a full-width-bar version that didn't match the "small toolbar" intent, which is why the shell went through a real redesign rather than shipping the first pass.
