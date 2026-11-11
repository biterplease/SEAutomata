<#
.SYNOPSIS
  Deploys the mod from this repository to the local Space Engineers Mods folder.

.DESCRIPTION
  1. Prepares a clean copy in .deploy\Automata, like deploy_steam.ps1 (New-DeployStage): modinfo.sbmi, Automata.ini,
     CREDITS.txt, thumb.png, Data, Models, Textures and Audio; under Data\Scripts only the *.cs files (no CLAUDE.md or
     other notes), without their comments (line breaks kept: compile errors point at the repository's lines).
  2. Sets LogLevel in the prepared Automata.ini (default: every level, Verbose|Info|Debug|Warning|Error); the
     repository's Automata.ini keeps the shipped default.
  3. Deletes the deployed copy (so files removed from the repo don't linger in the game).
  4. Copies the prepared copy to the game's Mods folder.
  Every step prints its time and how long it took.

.PARAMETER Target
  The mod's folder under the game's Mods folder. Default: %APPDATA%\SpaceEngineers\Mods\Automata

.PARAMETER LogLevel
  The deployed ini's LogLevel: any of Verbose, Info, Debug, Warning, Error, Quiet, separated by | or , (quote it:
  -LogLevel 'Error|Warning'). Default: all levels. 'Shipped' leaves the ini's own value.
#>
[CmdletBinding()]
param(
    [string]$Target = (Join-Path $env:APPDATA 'SpaceEngineers\Mods\Automata'),
    [string]$LogLevel = 'Verbose|Info|Debug|Warning|Error'
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\deploy_common.ps1"   # New-DeployStage

# The log level to deploy: names checked, normalised to a|b|c
$shippedLevel = ($LogLevel.Trim() -ieq 'Shipped')
if (-not $shippedLevel)
{
    $validLevels = 'Verbose', 'Info', 'Debug', 'Warning', 'Error', 'Quiet'
    $levels = New-Object System.Collections.Generic.List[string]
    foreach ($part in ($LogLevel -split '[|,;\s]+'))
    {
        if ($part -eq '') { continue }
        $match = $validLevels | Where-Object { $_ -ieq $part }
        if (-not $match) { throw "Unknown log level '$part' (use $($validLevels -join ', '), or 'Shipped')." }
        if (-not $levels.Contains($match)) { $levels.Add($match) }
    }
    if ($levels.Count -eq 0) { throw "No log level given (use $($validLevels -join ', '), or 'Shipped')." }
    $LogLevel = $levels -join '|'
}
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$script:lastStep = 0.0

function Write-Step([string]$Message)
{
    $now = $clock.Elapsed.TotalSeconds
    Write-Host ('[{0:HH:mm:ss.fff}] +{1,6:N2} s  {2}' -f (Get-Date), ($now - $script:lastStep), $Message)
    $script:lastStep = $now
}

$source = Join-Path $PSScriptRoot 'Automata'
if (-not (Test-Path -LiteralPath (Join-Path $source 'modinfo.sbmi')))
{
    throw "Mod sources not found in '$source' (modinfo.sbmi is missing)."
}
Write-Step "Deploy started: $source -> $Target"

# 1. Prepared in .deploy\Automata
New-DeployStage
Write-Step "Prepared in $($script:StageDir)"

# 2. The log level of the prepared ini (the repository's Automata.ini keeps the shipped default)
$deployedIni = Join-Path $script:StageDir 'Automata.ini'
if (-not $shippedLevel -and (Test-Path -LiteralPath $deployedIni))
{
    $iniBytes = [System.IO.File]::ReadAllBytes($deployedIni)
    $iniBom = ($iniBytes.Length -ge 3 -and $iniBytes[0] -eq 0xEF -and $iniBytes[1] -eq 0xBB -and $iniBytes[2] -eq 0xBF)
    $iniEncoding = New-Object System.Text.UTF8Encoding($iniBom)
    $iniText = [System.IO.File]::ReadAllText($deployedIni)
    $patched = [System.Text.RegularExpressions.Regex]::Replace($iniText, '(?m)^LogLevel=.*?(\r?)$', ('LogLevel=' + $LogLevel + '$1'))
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($iniText, '(?m)^LogLevel=')) { Write-Warning 'LogLevel not found in the deployed Automata.ini: log levels left as shipped' }
    else { [System.IO.File]::WriteAllText($deployedIni, $patched, $iniEncoding) }
    Write-Step "Deployed ini: LogLevel=$LogLevel (the repository's stays as shipped)"
}

# 3. The deployed copy goes
if (Test-Path -LiteralPath $Target)
{
    for ($attempt = 1; $attempt -le 5; $attempt++)
    {
        try
        {
            Remove-Item -LiteralPath $Target -Recurse -Force -ErrorAction Stop
            break
        }
        catch
        {
            if ($attempt -eq 5) { throw }
            Write-Step "Could not delete the deployed copy yet (attempt $attempt): $($_.Exception.Message)"
            Start-Sleep -Milliseconds 500
        }
    }
}
if (Test-Path -LiteralPath $Target)
{
    throw "The old deployed copy is still there: '$Target' (is the game running?)."
}
Write-Step 'Old deployed copy deleted'

# 4. Copy
New-Item -ItemType Directory -Path $Target -Force | Out-Null
Copy-Item -Path (Join-Path $script:StageDir '*') -Destination $Target -Recurse -Force
Write-Step 'Copied to the Mods folder'

$copied = @(Get-ChildItem -LiteralPath $Target -Recurse -File)
$megabytes = ($copied | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Step ('Deployed {0} files ({1:N1} MB)' -f $copied.Count, $megabytes)
Write-Host ('[{0:HH:mm:ss.fff}] Done in {1:N2} s' -f (Get-Date), $clock.Elapsed.TotalSeconds)
