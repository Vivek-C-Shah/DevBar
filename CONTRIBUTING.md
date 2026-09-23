# Contributing to DevBar

The most useful contribution is a module. The contract is five methods and the
whole point of the SDK is that it stays that small.

## The one rule

**Nothing runs while the bar is collapsed.**

Start your timers in `OnExpanded()`, stop them in `OnCollapsed()`. That is the
entire performance contract, and it is why the bar costs 0.0% CPU for the ~99%
of the day nobody is looking at it. A module that polls in the background, holds
a socket open, or animates while collapsed will not be merged, however good it
is otherwise.

Anything that scans the system — shelling out, enumerating processes, hitting a
socket — goes off the UI thread with `Task.Run`, so a slow scan on a loaded box
never stalls the animation.

## Building a module

Copy the closest built-in from `src/DevBar/Modules/`:

- **`Ports/`** — shells out or scans local state.
- **`Shelf/`** — mostly drag-and-drop UI.
- **`Docker/`** — talks to something that might not be installed, and needs
  honest "not installed" versus "not running" states.

Read [docs/architecture.md](docs/architecture.md) for the contract, and
[docs/style-lock.md](docs/style-lock.md) before you write any XAML: colours,
type and radii are design tokens, not per-module choices.

A module can also live entirely outside this repo — build a DLL against
`DevBar.Sdk` and drop it into `%LOCALAPPDATA%\DevBar\modules\`. You do not need
a PR here to ship one.

## Running it

```powershell
dotnet build DevBar.sln -c Release
.\src\DevBar\bin\Release\net8.0-windows10.0.19041.0\DevBar.exe --demo clipboard
```

`--demo <module-id>` launches pinned open on your module so you can skip the
hover dance while iterating. `--shelf-seed "C:\a.png;C:\b.txt"` pre-populates
the Shelf.

## Before you open a PR

- `dotnet build DevBar.sln -c Release` is clean.
- The bar still reads 0.0% CPU collapsed. The measurement command is in
  [docs/performance.md](docs/performance.md); run it with no other copy of
  DevBar running.
- New user-visible strings sound like the rest of the app: plain, specific, no
  exclamation marks, no "Oops".
- One change per PR. A module and a shell refactor in the same branch is two
  PRs.

## Reporting things

Bugs and ideas both go in [Issues](https://github.com/Vivek-C-Shah/DevBar/issues).
For a bug, the three things that actually help are your Windows version, what
you expected, and what happened instead. If it is a rendering or performance
problem, say whether Windows' "Transparency effects" setting is on or off — it
changes the whole rendering path.

Issues labelled **good first issue** are self-contained and have the shape of
the work described in them.

## Licence

MIT. By contributing you agree your work ships under it.
