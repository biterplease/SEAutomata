<#
.SYNOPSIS
  Deploys the mod from this repository to the local Space Engineers Mods folder.

.DESCRIPTION
  1. Deletes the deployed copy (so files removed from the repo don't linger in the game).
  2. Copies modinfo.sbm, Automata.ini, CREDITS.txt, Data, Models, Textures and Audio.
  Every step prints its time and how long it took.

.PARAMETER Target
  The mod's folder under the game's Mods folder. Default: %APPDATA%\SpaceEngineers\Mods\Automata
#>
[CmdletBinding()]
param(
    [string]$Target = (Join-Path $env:APPDATA 'SpaceEngineers\Mods\Automata')
)

$ErrorActionPreference = 'Stop'
$clock = [System.Diagnostics.Stopwatch]::StartNew()
$script:lastStep = 0.0

function Write-Step([string]$Message)
{
    $now = $clock.Elapsed.TotalSeconds
    Write-Host ('[{0:HH:mm:ss.fff}] +{1,6:N2} s  {2}' -f (Get-Date), ($now - $script:lastStep), $Message)
    $script:lastStep = $now
}

$source = Join-Path $PSScriptRoot 'Automata'
if (-not (Test-Path -LiteralPath (Join-Path $source 'modinfo.sbm')))
{
    throw "Mod sources not found in '$source' (modinfo.sbm is missing)."
}
Write-Step "Deploy started: $source -> $Target"

# 1. The deployed copy goes first
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

# 2. Copy
New-Item -ItemType Directory -Path $Target -Force | Out-Null

foreach ($name in 'modinfo.sbm', 'Automata.ini', 'CREDITS.txt')
{
    $path = Join-Path $source $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $Target -Force }
    else { Write-Warning "Skipped $name (not in the repository)" }
}
Write-Step 'Mod files copied'

foreach ($name in 'Data', 'Models', 'Textures', 'Audio')
{
    $path = Join-Path $source $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $Target -Recurse -Force }
    else { Write-Warning "Skipped $name (not in the repository)" }
    Write-Step "$name copied"
}

$copied = @(Get-ChildItem -LiteralPath $Target -Recurse -File)
$megabytes = ($copied | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Step ('Deployed {0} files ({1:N1} MB)' -f $copied.Count, $megabytes)
Write-Host ('[{0:HH:mm:ss.fff}] Done in {1:N2} s' -f (Get-Date), $clock.Elapsed.TotalSeconds)
