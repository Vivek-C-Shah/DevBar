<#
.SYNOPSIS
    Wires DevBar's Claude Code module up to real session state, in one command.

.DESCRIPTION
    Copies the hook script to %LOCALAPPDATA%\DevBar\hooks\ and merges the hook
    entries into your Claude Code settings. Without this, DevBar can only guess
    at session state by looking for terminal windows with "claude" in the title.

    Safe to run twice: existing DevBar hook entries are replaced, anything else
    in your settings is left alone, and the file is backed up first.

.PARAMETER Scope
    User (default) writes ~/.claude/settings.json, so every project reports in.
    Project writes .claude/settings.json in the current folder instead.

.EXAMPLE
    .\scripts\claude-hooks\install.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('User', 'Project')]
    [string]$Scope = 'User'
)

$ErrorActionPreference = 'Stop'

$hookDir = Join-Path $env:LOCALAPPDATA 'DevBar\hooks'
$hookPath = Join-Path $hookDir 'devbar-claude-state.ps1'
New-Item -ItemType Directory -Force -Path $hookDir | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'devbar-claude-state.ps1') $hookPath -Force
Write-Host "hook script -> $hookPath"

$settingsPath = if ($Scope -eq 'User') {
    Join-Path $env:USERPROFILE '.claude\settings.json'
} else {
    Join-Path (Get-Location) '.claude\settings.json'
}
New-Item -ItemType Directory -Force -Path (Split-Path $settingsPath) | Out-Null

$settings = if (Test-Path $settingsPath) {
    Copy-Item $settingsPath "$settingsPath.bak" -Force
    Write-Host "backed up existing settings -> $settingsPath.bak"
    Get-Content $settingsPath -Raw | ConvertFrom-Json
} else {
    [pscustomobject]@{}
}

function New-HookEntry([string]$state, [string]$matcher) {
    $cmd = [pscustomobject]@{
        type    = 'command'
        command = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$hookPath`" -State $state"
    }
    $entry = [ordered]@{}
    if ($matcher) { $entry.matcher = $matcher }
    $entry.hooks = @($cmd)
    [pscustomobject]$entry
}

# Which Claude Code event means which DevBar state. "waiting" is the one worth
# having: it is the whole reason to glance at the bar, and title-scraping
# cannot see it.
$wanted = [ordered]@{
    SessionStart     = (New-HookEntry 'idle'    $null)
    UserPromptSubmit = (New-HookEntry 'working' $null)
    PreToolUse       = (New-HookEntry 'working' '*')
    Notification     = (New-HookEntry 'waiting' $null)
    Stop             = (New-HookEntry 'waiting' $null)
    SessionEnd       = (New-HookEntry 'end'     $null)
}

if (-not $settings.PSObject.Properties['hooks']) {
    $settings | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
}
$hooks = $settings.hooks

foreach ($event in $wanted.Keys) {
    $existing = @()
    if ($hooks.PSObject.Properties[$event]) {
        # Drop any previous DevBar entry, keep everything else the user has.
        $existing = @($hooks.$event | Where-Object {
            -not ($_.hooks | Where-Object { $_.command -like '*devbar-claude-state.ps1*' })
        })
    }
    $merged = @($existing) + @($wanted[$event])
    if ($hooks.PSObject.Properties[$event]) {
        $hooks.$event = $merged
    } else {
        $hooks | Add-Member -NotePropertyName $event -NotePropertyValue $merged
    }
}

$settings | ConvertTo-Json -Depth 12 | Set-Content -Path $settingsPath -Encoding UTF8
Write-Host "hooks merged -> $settingsPath"
Write-Host ""
Write-Host "Start a new Claude Code session; DevBar's Claude tab will show it as working or waiting."
