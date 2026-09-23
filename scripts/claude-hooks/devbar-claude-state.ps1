<#
.SYNOPSIS
    Tells DevBar what this Claude Code session is doing.

.DESCRIPTION
    Claude Code runs this on its hook events. It writes one small JSON file per
    session into %LOCALAPPDATA%\DevBar\claude-sessions\, which is the reliable
    tier of DevBar's Claude Code module. Without it the module falls back to
    looking for terminal windows with "claude" in the title, which cannot tell
    working from waiting.

    The hook payload arrives as JSON on stdin and carries session_id and cwd.
    Nothing is sent anywhere: the file is local, and DevBar ignores any file
    older than 12 hours.

.PARAMETER State
    working | waiting | idle, or "end" to remove this session's file.

.EXAMPLE
    See settings.json in this folder for the wiring.
#>
# Deliberately NOT [CmdletBinding()]: Claude Code pipes the hook payload in on
# stdin, and an advanced script tries to bind that to a parameter and fails
# with "input object cannot be bound to any parameters" before a line of this
# runs. Plain param + an explicit stdin read is what actually works.
param(
    [Parameter(Mandatory)]
    [ValidateSet('working', 'waiting', 'idle', 'end')]
    [string]$State
)

$ErrorActionPreference = 'Stop'

# Never let a hook failure interrupt the session. A status file is a nicety;
# a Claude Code turn dying because a toolbar could not write JSON is not.
try {
    $raw = if ([Console]::IsInputRedirected) { [Console]::In.ReadToEnd() } else { '' }
    $payload = if ($raw.Trim()) { $raw | ConvertFrom-Json } else { $null }

    $sessionId = if ($payload -and $payload.session_id) { [string]$payload.session_id } else { "pid-$PID" }
    $cwd = if ($payload -and $payload.cwd) { [string]$payload.cwd } else { (Get-Location).Path }

    # One file per session, named by session id so re-runs overwrite rather
    # than pile up. Strip anything that is not filename-safe.
    $safe = ($sessionId -replace '[^A-Za-z0-9._-]', '')
    if (-not $safe) { $safe = "session" }

    $dir = Join-Path $env:LOCALAPPDATA 'DevBar\claude-sessions'
    $file = Join-Path $dir "$safe.json"

    if ($State -eq 'end') {
        if (Test-Path $file) { Remove-Item $file -Force }
        return
    }

    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    # DevBar clicks this pid to focus the terminal, so it wants the window the
    # session is sitting in, not this short-lived PowerShell. Walk up the
    # parent chain to the first process that actually owns a window.
    $pidToFocus = $PID
    try {
        $current = Get-CimInstance Win32_Process -Filter "ProcessId = $PID"
        for ($i = 0; $i -lt 6 -and $current; $i++) {
            $parentId = [int]$current.ParentProcessId
            if ($parentId -le 0) { break }
            $parent = Get-Process -Id $parentId -ErrorAction SilentlyContinue
            if (-not $parent) { break }
            $pidToFocus = $parent.Id
            if ($parent.MainWindowHandle -ne 0) { break }
            $current = Get-CimInstance Win32_Process -Filter "ProcessId = $parentId"
        }
    } catch { }

    $doc = [ordered]@{
        project = Split-Path $cwd -Leaf
        pid     = $pidToFocus
        state   = $State
    }

    # Write then move, so DevBar never reads a half-written file.
    $tmp = "$file.tmp"
    $doc | ConvertTo-Json -Compress | Set-Content -Path $tmp -Encoding UTF8
    Move-Item -Path $tmp -Destination $file -Force
} catch {
    exit 0
}
