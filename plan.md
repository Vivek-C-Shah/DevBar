# DevBar — a hover-expand command strip for Windows developers

An open-source, edge-docked bar that sits nearly invisible at the top of the screen and expands into a row of live developer modules on hover. Think "taskbar, but it only shows up when you want it, and it's built for people who live in terminals, not the Start menu."

---

## 1. Why this is worth building

There's no shortage of Windows customization tools (Zebar, JaxCore/Rainmeter, RTSS-style overlays), and no shortage of single-purpose utilities (clipboard managers, media flyouts). What's missing is the *combination*: a low-friction, always-available strip that surfaces the handful of things a developer checks 50 times a day — is my agent session done, did Slack blow up, what's in my clipboard, is Docker still running that container — without opening a single window.

The differentiators:
- **Hover-triggered, not click-triggered.** Zero cognitive overhead — glance up, it's there; look away, it's gone.
- **Modular by design.** Each capability is a plugin. The core team ships a few; the community ships the rest.
- **Ephemeral where it should be.** The shelf resets on restart on purpose — it's a scratch space, not another thing to manage.

---

## 2. UX principles

1. **Idle state is nearly invisible.** A 6–10px sliver, barely more than a shadow line, docked to the top edge via the Win32 `AppBar` API so it reserves real screen space (apps won't render under it, same as the taskbar).
2. **Hover expands, doesn't pop.** 150–250ms ease-out height/opacity transition. No bounce, no overshoot — this lives on screen all day, so restraint matters more than delight.
3. **Carousel, not a grid.** Expanded state shows one module in focus, not a row of shrunk cards. A grid forces every module to compress to fit, which makes each one less legible the more you add. A carousel keeps every module full-size regardless of how many are installed — arrows + dot indicators + swipe (trackpad two-finger or touch) to page between them. Scales cleanly from 4 modules to 40.
4. **Position is sticky, not sequential-only.** Last-viewed module reopens on next hover (people check the same 1–2 things most of the time); paging through the rest is there when you want it. Optional: hotkey-assign a module to jump straight to it (e.g. hold Alt while hovering to jump to Docker).
5. **Nothing steals focus.** Modules are glanceable and clickable, but hovering never grabs keyboard focus from whatever you're working in.
6. **Leaving is instant, entering has a tiny buffer.** ~120ms show delay (avoids flicker on incidental cursor passes near the top edge), but hide immediately on mouse-leave — err toward getting out of the way.
7. **Pin option.** Any module (or the whole bar) can be pinned open for a session — useful when actively waiting on a build or a Claude Code run.
8. **Dark/light aware, respects Windows accent color**, but stays visually flat — no gradients or drop shadows, this needs to disappear into the desktop, not compete with it.

---

## 3. Tech stack recommendation

| Layer | Choice | Why |
|---|---|---|
| UI framework | **WinUI 3 (.NET 8, C#)** | Native look, good perf, first-class Fluent styling, active Microsoft support. Electron would be faster to prototype but a permanently-on-screen app needs the lower memory/CPU footprint of a native shell. |
| Carousel paging | `FlipView` (built into WinUI/UWP) or a custom `Grid` + `TranslateTransform` with an `ExpressionAnimation`/`Storyboard` for the slide | `FlipView` gives swipe, arrow buttons, and indexing for free — cheapest path to the paging behavior. A hand-rolled version is only worth it if `FlipView`'s default chrome fights the flat aesthetic. |
| Screen docking | **Win32 `SHAppBarMessage` (AppBar API)** via P/Invoke | This is how the Windows taskbar itself reserves space — same mechanism, so other windows correctly avoid overlapping it. `AppSwitcherBar` (open source) is a good reference implementation to study. |
| Hover detection | Low-level mouse hook (`WH_MOUSE_LL`) or simpler: `MouseEnter`/`MouseLeave` on a tall invisible hit-zone above the visible sliver | Needs to detect proximity even though the visible bar is only ~10px — hit-test area should be a bit taller than what's rendered. |
| Module plugin system | C# interface (`IDevBarModule`) + MEF (`System.Composition`) for discovery, or a simpler manifest-based loader (`module.json` + DLL) if MEF feels heavy | Lets third parties drop a DLL in a `modules/` folder without recompiling the shell. Prioritize a dead-simple contract over cleverness — this is the thing that determines whether people actually contribute modules. |
| Settings/config | JSON file in `%LOCALAPPDATA%\DevBar\config.json` | Matches convention used by comparable open-source tools (see the Claude usage widget's own config pattern). |
| Media control | Windows `GlobalSystemMediaTransportControlsSessionManager` (SMTC) API | Already tracks every app's now-playing state — you're skinning existing OS data, not reinventing it. |
| Clipboard | `AddClipboardFormatListener` + `GetClipboardData` | Standard Win32 clipboard hook; store history in-memory with a rolling cap (e.g. last 25 items), persist optionally. |
| Docker module | Docker Desktop's named pipe API (`npipe:////./pipe/docker_engine`) via `Docker.DotNet` | Local Docker Engine API — no cloud dependency, works offline, same interface `docker ps` uses under the hood. |
| Git status module | Shell out to `git status --porcelain` / `git rev-list --left-right --count` per watched repo, on a polling or file-watcher trigger | Avoid a full libgit2 dependency for v1 — shelling out is simpler and fast enough for a handful of repos. |
| Process/port module | `netstat`-equivalent via `IPGlobalProperties.GetActiveTcpListeners()` + cross-reference PIDs | Managed API exists, no need to parse `netstat` output. |
| Claude Code session tracker | Watch the CLI's session/log directory (or a lightweight local status file the CLI could optionally emit) for active/idle state per project | This one needs the most exploration — starts as "which terminal windows have a Claude Code process running" via process enumeration, can get smarter later. |
| Distribution | MSIX or a simple installer (Inno Setup / WiX) + winget package | winget listing matters a lot for organic open-source adoption in this space. |

---

## 4. Module system architecture

```
DevBar.Core        →  AppBar docking, hover/expand shell, module host, config
DevBar.SDK         →  IDevBarModule contract, shared UI primitives, module manifest schema
DevBar.Modules.*    →  first-party modules, each a separate assembly
  ├─ ClaudeCode
  ├─ Clipboard
  ├─ Shelf
  ├─ Media
  ├─ Docker
  ├─ GitStatus
  ├─ PortWatch
  └─ Slack
Community modules   →  drop a folder in %LOCALAPPDATA%\DevBar\modules\, same contract
```

**Module contract (sketch):**

```csharp
public interface IDevBarModule
{
    string Id { get; }
    string DisplayName { get; }
    string IconGlyph { get; }
    UserControl BuildCard();       // the widget shown when expanded
    Task RefreshAsync();           // called on a timer / event trigger
    ModuleWidth PreferredWidth { get; }  // narrow / medium / wide
}
```

Keeping this contract small is the whole point — a module author should be able to build one in an afternoon.

---

## 5. Module roster

### Your ideas
| Module | What it shows | Notes |
|---|---|---|
| **Claude Code sessions** | Active/idle/waiting state per project, click to focus terminal | Novel — nothing like this exists yet |
| **Slack** | Unread count, DND toggle, maybe last 3 DMs | Presence + urgency only, not a full client |
| **Clipboard carousel** | Recent copies, type-aware (code/URL/image) | Standalone value even outside the bar |
| **The shelf** | Ephemeral drag-and-drop drop zone for files/images, clears on restart | Best original idea here — Windows has no equivalent to macOS's Shelf/Yoink |
| **Media control** | Now playing + transport controls | Cheap to build via SMTC |
| **Docker containers** *(new)* | Running containers, up/down state, one-click start/stop/logs tail | Straightforward via Docker Desktop's local API, high daily-use value for anyone doing local dev with compose stacks |

### My additions
| Module | What it shows | Why it earns a slot |
|---|---|---|
| **Port/process watch** | Bound ports (3000, 5173, 8080...) + one-click kill | Replaces a dozen `npx kill-port` runs a day |
| **Git status strip** | Dirty/clean, ahead/behind, branch, per watched repo | Glanceable without alt-tabbing to a terminal |
| **Build/CI pulse** | Quiet dot that flips color when a local build or CI run finishes | Stops people babysitting a terminal tab |
| **PR/review radar** | Assigned PRs + CI status pulled from GitHub | Keeps review requests from getting lost in Slack noise |

**Suggested v1 scope (don't build all eight at once):** Shelf + Clipboard + Claude Code sessions + Media. These four are the highest novelty-to-effort ratio and give people a reason to install on day one. Docker, Git status, and Port watch are strong v1.1 candidates — technically simple, high daily utility. Slack, CI pulse, and PR radar involve OAuth/API integration and are better as v2, once the plugin system is proven.

---

## 6. Roadmap

**Phase 0 — shell only (2–3 weeks)**
AppBar docking, hover-expand animation, empty module host, settings file. Ship with zero modules just to prove the shell feels good — this is the part that has to be *right*, everything else is replaceable.

**Phase 1 — MVP modules (3–4 weeks)**
Shelf, Clipboard, Media, Claude Code session tracker. Public repo, README, first release on winget.

**Phase 2 — dev-workflow modules (2–3 weeks)**
Docker, Git status, Port watch. This is where it goes from "nice utility" to "I keep this open all day."

**Phase 3 — plugin ecosystem (ongoing)**
Publish the SDK + module template repo, write a "build your first module" guide, open a modules registry (even just a curated list in the README to start). This is the point where growth stops being bottlenecked by your own build capacity.

---

## 7. Open-source considerations

- **License:** MIT — matches the rest of this ecosystem (Zebar, the Claude usage widget, most desktop-widget projects on GitHub) and removes friction for corporate contributors.
- **Module template repo:** a `dotnet new` template or a minimal starter repo so a new module is a five-minute `git clone` + fill-in-the-blanks, not a spelunking exercise through your core codebase.
- **Telemetry:** none by default, or fully opt-in and disclosed — this app sits in a position to see clipboard contents and file drags, so trust has to be earned explicitly, not assumed.
- **Naming collision check:** worth a quick search before launch — there's already an unrelated "DevBar" browser extension and a Firefox dev toolbar from years back, so a distinct name will save confusion (a few options: `Fringe`, `Ledge`, `Overhang`, `Threshold`).

---

## 8. Open questions worth deciding early

- Does the shelf persist within a session across multiple monitors, or is it per-monitor?
- For the Claude Code tracker: is there (or should there be) a lightweight status file the CLI writes, versus purely inferring state from process/terminal inspection?
- Single bar instance vs. one per monitor — likely single bar on primary display for v1, configurable later.