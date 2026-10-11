<#
.SYNOPSIS
  Deploys the mod exactly like deploy_local.ps1, then removes every Log.* call from the DEPLOYED scripts.

.DESCRIPTION
  For measuring what logging costs: run the same scenario with deploy_local.ps1 (logging as configured)
  and with this script (no logging code left), and compare. The repository is never touched.

  Removed: Log.Verbose / Info / Debug / Warning / Error and the specialised Log.LogPathfinding, LogDroneNetwork,
  LogDroneOrders, IncreaseIndent, DecreaseIndent, ResetIndent calls. Each becomes an empty block {} on its line (line
  numbers stay the same), so the arguments (string formatting, boxing) are no longer evaluated either.
  Also: Log.DebugEnabled becomes false and Log.LogLevel becomes 0, so the Debug-only blocks and the PerfStats
  timers (which only exist to be logged) are off. Not touched: the LCD logs, Log.Initialize / Close, Util\Logging.cs.

  Run deploy_local.ps1 again to get the normal build back.

.PARAMETER Target
  The mod's folder under the game's Mods folder. Default: %APPDATA%\SpaceEngineers\Mods\Automata
#>
[CmdletBinding()]
param(
    [string]$Target = (Join-Path $env:APPDATA 'SpaceEngineers\Mods\Automata')
)

$ErrorActionPreference = 'Stop'
$clock = [System.Diagnostics.Stopwatch]::StartNew()

# 1. The normal deploy
& (Join-Path $PSScriptRoot 'deploy_local.ps1') -Target $Target

# 2. Strip the deployed copy
Write-Host ('[{0:HH:mm:ss.fff}] Removing Log.* calls from the deployed scripts' -f (Get-Date))
if (-not ('LogStripper' -as [type]))
{
    Add-Type -Path (Join-Path $PSScriptRoot 'tools\LogStripper.cs')
}

$scripts = Join-Path $Target 'Data\Scripts'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$removed = 0
$kept = 0
$changedFiles = 0
$notes = New-Object System.Collections.Generic.List[string]

foreach ($file in Get-ChildItem -LiteralPath $scripts -Recurse -Filter '*.cs')
{
    if ($file.FullName.EndsWith('Util\Logging.cs')) { continue }   # the logger itself stays

    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $skip = 0
    if ($bom) { $skip = 3 }
    $text = $utf8.GetString($bytes, $skip, $bytes.Length - $skip)

    $relative = $file.FullName.Substring($scripts.Length).TrimStart('\')
    $result = [LogStripper]::Strip($text, $relative)
    $removed += $result.Removed
    $kept += $result.Kept
    foreach ($note in $result.Notes) { $notes.Add($note) }

    if ($result.Text -cne $text)
    {
        $encoding = $utf8
        if ($bom) { $encoding = $utf8Bom }
        [System.IO.File]::WriteAllText($file.FullName, $result.Text, $encoding)
        $changedFiles++
    }
}

foreach ($note in $notes) { Write-Warning $note }
Write-Host ''
Write-Host ('NO-LOG BUILD deployed: {0} Log calls removed from {1} files, {2} left in.' -f $removed, $changedFiles, $kept) -ForegroundColor Yellow
Write-Host 'Run deploy_local.ps1 to restore the normal build.' -ForegroundColor Yellow
Write-Host ('[{0:HH:mm:ss.fff}] Total {1:N2} s' -f (Get-Date), $clock.Elapsed.TotalSeconds)
