# Architecture

```
DevBar.Sdk          IDevBarModule contract - five methods, that's the whole surface
DevBar (app)
  Core/              AppBar-free window shell, native interop, config, clipboard hook
  Modules/*/         one folder per built-in module (Clipboard, Shelf, Claude, Ports,
                     Docker, GitStatus, CiPulse, Media, Jarvis)
  Themes/            design tokens (colors, type, radii) - see docs/style-lock.md
```

## Writing a module

A module is a class implementing:

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

Drop a compiled DLL implementing it into `%LOCALAPPDATA%\DevBar\modules\` and DevBar picks it up on next launch — no core recompile needed.

A module template repo is a `v1.2` item. For now, the built-in modules under `src/DevBar/Modules/` are the reference implementations to copy from:

- **`Ports/`** if your module shells out or scans local state.
- **`Shelf/`** if it is mostly drag-and-drop UI.
- **`Docker/`** if it talks to something that might not be installed, and you need honest "not installed" versus "not running" states.

The one rule that matters: **nothing runs while collapsed.** Start your timers in `OnExpanded()`, stop them in `OnCollapsed()`. See [performance.md](performance.md) for why that is the whole point.

## Jarvis

Jarvis is a module like any other, but it reaches across the rest of them: its tools are backed by the same code that powers Ports, Docker, Git status, Clipboard, Media and Claude Code, plus tools for apps, screen, mail, calendar and shell.

- Speech in is on-device (sherpa-onnx), so the microphone stays shut until you press the shortcut or say the wake word.
- The model chain is configurable, and falls through to the next provider on a rate limit.
- Destructive tools require a spoken confirmation before they run.

See [jarvis-plan.md](jarvis-plan.md) for the original design and the decisions behind it.
