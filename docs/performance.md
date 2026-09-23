# Performance, and the one honest tension

This was the actual design constraint, not a footnote. The plan going in was explicit: *don't build a tool that costs more attention than it saves.* So the numbers below are measured on a dev machine, not asserted.

| | Idle (collapsed) | Expanded (Acrylic blur + mesh gradient active) |
|---|---|---|
| CPU | **0.0%** over a 5–10s sample (`Get-Process` `TotalProcessorTime` delta) | **~45–50%** of one core, sustained |
| Working set | ~90–140MB | same ballpark |
| Threads | ~30 | ~30 |

> The expanded figure predates two changes that target exactly it: the blurred
> blobs are now bitmap-cached, so drifting moves a rasterised layer instead of
> re-running a 24px blur every frame, and the drift runs at 24fps rather than
> the compositor's 60. It has not been re-measured since. To take your own
> reading, with no other copy of DevBar running:
>
> ```powershell
> $p = Start-Process .\src\DevBar\bin\Release\net8.0-windows10.0.19041.0\DevBar.exe `
>        -ArgumentList "--demo clipboard" -PassThru
> Start-Sleep 8; $p.Refresh(); $t0 = $p.TotalProcessorTime; $w0 = Get-Date
> Start-Sleep 12; $p.Refresh()
> "{0:N1}% of one core" -f (($p.TotalProcessorTime - $t0).TotalSeconds / ((Get-Date) - $w0).TotalSeconds * 100)
> Stop-Process -Id $p.Id
> ```

That expanded-state number is not a typo, and it is the one honest tension in this whole design: real Windows Acrylic blur-behind is genuinely expensive. DWM has to keep re-sampling whatever is behind the window for as long as it is on, and independently-drifting blurred mesh blobs add more compositor work on top of that.

This is why the entire discipline of this app is making sure that cost only exists for the few seconds a developer is actually looking at the expanded card. `GlassEffect.Enable`/`Disable` and `StartBlobDrift`/`StopBlobDrift` are called from `Expand()`/`Collapse()` specifically so it drops back to 0.0% the instant the bar collapses, never during the ~99% of the day it sits idle as a small pill.

Three ways to spend less while expanded, in order of how much they cost you visually:

- **`"meshDrift": false`** in `config.json` keeps the glass and the mesh but stops the blobs drifting. The card looks the same standing still; only the slow motion goes.
- **`"meshBlobs": 2`** (default 4) halves the blurred layers the compositor has to composite.
- **Windows' own "Transparency effects"** setting (Settings → Accessibility → Visual effects) turns the whole thing off: no blur, no mesh, a flat opaque panel. DevBar reads that setting and respects it automatically.

## How the idle number stays at zero

- **Nothing polls while collapsed.** Every module's timer starts in `OnExpanded()` and stops in `OnCollapsed()` — by design, not by convention (see `IDevBarModule`).
- **Clipboard and hover are 100% event-driven** — `AddClipboardFormatListener` and native mouse-enter/leave, no polling loop anywhere in the shell.
- **Every system-scanning module runs off the UI thread** (`Task.Run`) — Ports, Claude Code, Docker and Git status all shell out or enumerate processes in the background, so a slow scan on a loaded dev box never stalls the animation.
- **No AppBar space reservation.** Early builds used the Win32 AppBar API (the same mechanism as the taskbar) to reserve the full screen width. It worked, but it is the wrong shape for a tool this small, and it meant every app on the machine had to respect a strip it did not need to. The current build is a plain topmost window sized to its own content, with `WM_NCHITTEST` making the space around the pill click-through.

## On "Liquid Glass"

Worth being precise about what this actually is, since the ask was for a specific, named design language (Apple's Liquid Glass) and Windows does not expose the same primitives macOS/iOS do:

- **Real backdrop blur, yes** — via `SetWindowCompositionAttribute` (Acrylic), genuinely blurring whatever is behind the window at the OS/compositor level.
- **"Blur radius 24," applied literally** — but to the bar's own mesh-gradient blobs (`BlurEffect Radius="24"` in WPF, which *does* expose a real pixel radius), not to the backdrop blur itself. Windows' Acrylic API does not take a radius parameter; its blur amount is fixed by the OS.
- **"Organic tint that shifts"** — soft, slowly-drifting radial-gradient blobs, asymmetrically placed and each on its own drift speed and direction so they never sync up, in hues rotated off your live Windows accent color, not a static gradient. What it is *not*: literal live-sampling of desktop pixel colors behind the window to drive the tint. That would mean continuous screen capture plus colour analysis, which is real, ongoing CPU cost of exactly the kind this app spent most of its effort eliminating — not a trade worth making for a decorative effect.
- **Accessibility fallback, real** — `AccessibilityHelper.PrefersReducedTransparency()` reads Windows' actual "Transparency effects" setting via `UISettings.AdvancedEffectsEnabled` and swaps in a fully opaque panel, no blur, no mesh, when it is off.
- The working-set floor (~90MB) is WPF + CLR baseline plus the Windows Runtime projections the accent-colour and Media modules touch once at startup — the honest cost of native UI on .NET, not something the bar wastes ongoing.

Electron would have made the first screen faster to build and the process afterwards heavier by 100+MB and non-zero at idle. That trade is why this is WPF.
