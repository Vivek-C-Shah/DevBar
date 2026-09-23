# Claude Code hooks for DevBar

DevBar's Claude Code module has two tiers. Out of the box it looks for terminal
windows with "claude" in the title, which tells you a session exists and nothing
more. These hooks give it the real thing: **working**, **waiting on you**, or
**idle**, per project.

"Waiting on you" is the reason to have the module at all, and title-scraping
cannot see it.

## Install

```powershell
.\scripts\claude-hooks\install.ps1
```

That copies `devbar-claude-state.ps1` to `%LOCALAPPDATA%\DevBar\hooks\` and
merges the hook entries into `~/.claude/settings.json`. Your existing settings
are backed up first and anything else in them is left alone. Run it again any
time; it replaces its own entries rather than stacking them.

Use `-Scope Project` to wire up only the current repo (`.claude/settings.json`)
instead of every session.

Start a new Claude Code session afterwards. Hooks are read at session start, so
an already-running session will not report in.

## What maps to what

| Claude Code event | DevBar state | Why |
|---|---|---|
| `SessionStart` | idle | The session exists; nothing is happening yet. |
| `UserPromptSubmit` | working | You just asked for something. |
| `PreToolUse` | working | It is running a tool. |
| `Notification` | waiting | It needs permission or input. This is the one you want. |
| `Stop` | waiting | It finished its turn, so the ball is back with you. |
| `SessionEnd` | *file removed* | The session is gone; stop listing it. |

## What it writes

One file per session at
`%LOCALAPPDATA%\DevBar\claude-sessions\<session-id>.json`:

```json
{ "project": "my-app", "pid": 31804, "state": "waiting" }
```

`project` is the folder name of the session's working directory. `pid` is the
nearest parent process that owns a window, so clicking the row in DevBar focuses
the terminal the session is actually in. Files are written atomically, so DevBar
never reads a half-written one, and anything older than 12 hours is ignored.

Nothing leaves your machine, and nothing runs except on those events.

## Doing it by hand

If you would rather wire it yourself, `settings.json` in this folder is the same
configuration to copy into your Claude Code settings. Replace the path with
wherever you put the script.

## Uninstall

Delete the DevBar entries from `~/.claude/settings.json` (they are the ones
whose command mentions `devbar-claude-state.ps1`), or restore the `.bak` the
installer wrote next to it.
