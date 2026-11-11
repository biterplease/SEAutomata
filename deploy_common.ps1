# Shared helpers for deploy_steam.ps1, deploy_modio.ps1 and deploy_local.ps1

$script:ModName = 'Automata'
$script:ModDir = Join-Path $PSScriptRoot $script:ModName
$script:ModInfoPath = Join-Path $script:ModDir 'modinfo.sbmi'
$script:DeployDir = Join-Path $PSScriptRoot '.deploy'
$script:StageDir = Join-Path $script:DeployDir $script:ModName   # the folder SEWorkshopTool pushes (named like the mod)
$script:ChangelogPath = Join-Path $script:DeployDir 'changelog.txt'

# What ships. Everything else in the mod folder (bin, obj, *.csproj, *.mdk*.ini, ...) is development only.
$script:ShippedFiles = @('modinfo.sbmi', 'Automata.ini', 'CREDITS.txt', 'thumb.png')
$script:ShippedFolders = @('Data', 'Models', 'Textures', 'Audio')
# Under Data\Scripts only the *.cs files ship (no CLAUDE.md or other notes), without their comments; the rest of Data
# (definitions, localization) ships whole.

# Removes everything but *.cs from <Root>\Data\Scripts, and the folders that leaves empty. 'Root': a folder holding Data\.
function Remove-NonScriptFiles {
    param([Parameter(Mandatory)][string]$Root)

    $scripts = Join-Path $Root 'Data\Scripts'
    if (-not (Test-Path -LiteralPath $scripts)) { return }
    $extra = @(Get-ChildItem -LiteralPath $scripts -Recurse -File | Where-Object { $_.Extension -ine '.cs' })
    $extra | Remove-Item -Force
    Get-ChildItem -LiteralPath $scripts -Recurse -Directory |
        Sort-Object { $_.FullName.Length } -Descending |
        Where-Object { -not (Get-ChildItem -LiteralPath $_.FullName -Force) } |
        Remove-Item -Force
    Write-Host ('Not shipped: {0} non-.cs file(s) under Data\Scripts' -f $extra.Count)
}

# Removes the comments from the *.cs files under <Root>\Data\Scripts (tools\CommentStripper.cs: strings and char
# literals kept; lines left empty by a removed comment go, so the game's compile errors no longer point at the
# repository's line numbers).
function Remove-CodeComments {
    param([Parameter(Mandatory)][string]$Root)

    $scripts = Join-Path $Root 'Data\Scripts'
    if (-not (Test-Path -LiteralPath $scripts)) { return }
    if (-not ('CommentStripper' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'tools\CommentStripper.cs')
    }
    $before = 0L
    $after = 0L
    $files = @(Get-ChildItem -LiteralPath $scripts -Recurse -File -Filter '*.cs')
    foreach ($file in $files) {
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
        $text = [System.IO.File]::ReadAllText($file.FullName)
        $stripped = [CommentStripper]::Strip($text)
        [System.IO.File]::WriteAllText($file.FullName, $stripped, (New-Object System.Text.UTF8Encoding($bom)))
        $before += $bytes.Length
        $after += (Get-Item -LiteralPath $file.FullName).Length
    }
    Write-Host ('Comments removed from {0} script(s): {1:N0} KB -> {2:N0} KB' -f $files.Count, ($before / 1KB), ($after / 1KB))
}

function Find-SEWorkshopTool {
    if ($env:SEWORKSHOPTOOL_PATH -and (Test-Path -LiteralPath $env:SEWORKSHOPTOOL_PATH)) {
        return (Resolve-Path -LiteralPath $env:SEWORKSHOPTOOL_PATH).Path
    }

    $candidates = @(
        'C:\Steam\steamapps\common\SpaceEngineers\SEWorkshopTool.exe',
        'C:\Steam\steamapps\common\SpaceEngineers\Bin64\SEWorkshopTool.exe'
    )

    foreach ($path in $candidates) {
        if (Test-Path -LiteralPath $path) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    throw @"
SEWorkshopTool.exe not found.
Install from https://github.com/Gwindalmir/SEWorkshopTool/releases
or set SEWORKSHOPTOOL_PATH to the full path of SEWorkshopTool.exe.
"@
}

function Get-ModVersion {
    param([string]$Path = $script:ModInfoPath)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "modinfo.sbmi not found: $Path"
    }

    [xml]$doc = Get-Content -LiteralPath $Path -Encoding UTF8
    $version = $doc.MyObjectBuilder_ModInfo.Version
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "No <Version> element in $Path"
    }

    return $version.Trim()
}

function Get-NextSemVer {
    param(
        [Parameter(Mandatory)]
        [string]$Version,
        [ValidateSet('patch', 'minor', 'major')]
        [string]$Bump = 'patch'
    )

    if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$') {
        throw "Version '$Version' is not semver x.y.z (got from modinfo.sbmi)."
    }

    $major = [int]$Matches[1]
    $minor = [int]$Matches[2]
    $patch = [int]$Matches[3]

    switch ($Bump) {
        'major' { return '{0}.0.0' -f ($major + 1) }
        'minor' { return '{0}.{1}.0' -f $major, ($minor + 1) }
        'patch' { return '{0}.{1}.{2}' -f $major, $minor, ($patch + 1) }
    }
}

function Set-ModVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Version,
        [string]$Path = $script:ModInfoPath
    )

    # Edit the element's text only: the file keeps its BOM, line endings and layout (XmlDocument.Save would reformat it)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.IO.File]::ReadAllText($Path)
    $pattern = '(<Version>)[^<]*(</Version>)'
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch($text, $pattern)) {
        throw "No <Version> element in $Path"
    }
    $patched = [System.Text.RegularExpressions.Regex]::Replace($text, $pattern, ('${1}' + $Version + '${2}'))
    [System.IO.File]::WriteAllText($Path, $patched, (New-Object System.Text.UTF8Encoding($bom)))
}

function Read-ChangelogMessage {
    param([string]$PresetMessage)

    if (-not [string]::IsNullOrWhiteSpace($PresetMessage)) {
        return $PresetMessage.Trim()
    }

    Write-Host ''
    Write-Host 'Changelog / change note (required for this deploy):' -ForegroundColor Cyan
    $message = Read-Host 'Message'
    if ([string]::IsNullOrWhiteSpace($message)) {
        throw 'Deploy cancelled: changelog message cannot be empty.'
    }

    return $message.Trim()
}

function Write-DeployChangelogFile {
    param(
        [Parameter(Mandatory)]
        [string]$Version,
        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not (Test-Path -LiteralPath $script:DeployDir)) {
        New-Item -ItemType Directory -Path $script:DeployDir | Out-Null
    }

    $body = @(
        "v$Version"
        ''
        $Message
    ) -join [Environment]::NewLine

    # UTF-8 without BOM (Set-Content -Encoding UTF8 writes one on Windows PowerShell 5.1)
    [System.IO.File]::WriteAllText($script:ChangelogPath, $body, (New-Object System.Text.UTF8Encoding($false)))
    return $script:ChangelogPath
}

# A clean copy of the shipped files under .deploy\Automata (deploy_steam, deploy_modio push it, deploy_local copies it to
# the game): never bin, obj, the csproj or the local ini files; the scripts without comments.
function New-DeployStage {
    if (Test-Path -LiteralPath $script:StageDir) {
        Remove-Item -LiteralPath $script:StageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $script:StageDir -Force | Out-Null

    foreach ($name in $script:ShippedFiles) {
        $path = Join-Path $script:ModDir $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $script:StageDir -Force }
        elseif ($name -eq 'modinfo.sbmi') { throw "modinfo.sbmi not found: $path" }
        else { Write-Warning "Not shipped: $name (not in the mod folder)" }
    }

    foreach ($name in $script:ShippedFolders) {
        $path = Join-Path $script:ModDir $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $script:StageDir -Recurse -Force }
        else { Write-Warning "Not shipped: $name (not in the mod folder)" }
    }
    Remove-NonScriptFiles -Root $script:StageDir
    Remove-CodeComments -Root $script:StageDir

    $files = @(Get-ChildItem -LiteralPath $script:StageDir -Recurse -File)
    $megabytes = ($files | Measure-Object -Property Length -Sum).Sum / 1MB
    Write-Host ('Staged {0} files ({1:N1} MB) in {2}' -f $files.Count, $megabytes, $script:StageDir)
}

# True when modinfo.sbmi already carries a mod.io id (SEWorkshopTool writes it after the first mod.io publish)
function Test-ModioPublished {
    $text = Get-Content -LiteralPath $script:ModInfoPath -Raw -Encoding UTF8
    return ($text -match '(?i)mod\.?io')
}

function Invoke-ModDeploy {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Steam', 'Modio')]
        [string]$Platform,

        [ValidateSet('patch', 'minor', 'major')]
        [string]$Bump = 'patch',

        [switch]$SkipVersionBump,
        [switch]$DryRun,
        [switch]$Force,
        [switch]$Create,
        [string]$Message
    )

    if (-not (Test-Path -LiteralPath $script:ModDir)) {
        throw "Mod folder not found: $($script:ModDir)"
    }

    if (-not (Test-Path -LiteralPath $script:ModInfoPath)) {
        throw "modinfo.sbmi not found. SEWorkshopTool needs this file with a published Workshop/mod.io id."
    }

    # Update an existing item unless -Create. mod.io: the first publish has no id yet, so it creates the item itself.
    $updateOnly = -not $Create
    if ($Platform -eq 'Modio' -and -not $Create -and -not (Test-ModioPublished)) {
        Write-Host 'modinfo.sbmi has no mod.io id yet: this deploy creates the mod.io item (as -Create).' -ForegroundColor Yellow
        $updateOnly = $false
    }

    $tool = Find-SEWorkshopTool
    $currentVersion = Get-ModVersion
    $newVersion = $currentVersion

    if (-not $SkipVersionBump) {
        $newVersion = Get-NextSemVer -Version $currentVersion -Bump $Bump
    }

    $changelogMessage = Read-ChangelogMessage -PresetMessage $Message
    $changelogFile = Write-DeployChangelogFile -Version $newVersion -Message $changelogMessage

    Write-Host ''
    Write-Host "Platform:   $Platform$(if (-not $updateOnly) { ' (create)' })"
    Write-Host "Mod:        $($script:ModDir)"
    Write-Host "Version:    $currentVersion -> $newVersion$(if ($SkipVersionBump) { ' (skipped bump)' })"
    Write-Host "Changelog:  $changelogFile"
    Write-Host "Tool:       $tool"
    if ($Platform -eq 'Modio') {
        Write-Host ''
        Write-Host 'mod.io via SEWorkshopTool is experimental. Changelog (--message) is documented for Steam;' -ForegroundColor Yellow
        Write-Host 'it may not appear on mod.io. Configure mod.io auth in SEWorkshopTool.exe.config if needed.' -ForegroundColor Yellow
    }

    if (-not $Force) {
        $confirm = Read-Host 'Deploy now? [Y/n]'
        if ($confirm -match '^[Nn]') {
            throw 'Deploy cancelled by user.'
        }
    }

    $bumped = (-not $SkipVersionBump -and -not $DryRun -and $newVersion -ne $currentVersion)
    if ($bumped) {
        Set-ModVersion -Version $newVersion
        Write-Host "Updated modinfo.sbmi version to $newVersion"
    }

    try {
        # Staged after the bump, so the pushed modinfo.sbmi carries the new version
        New-DeployStage

        $toolArgs = @(
            'push',
            '--mods', $script:StageDir,
            '--no-compile',
            '--message', $changelogFile
        )

        if ($updateOnly) {
            $toolArgs += '--update-only'
        }

        if ($Platform -eq 'Modio') {
            $toolArgs += '--modio'
        }

        if ($DryRun) {
            $toolArgs += '--dry-run'
        }

        Write-Host ''
        Write-Host "Running: $tool $($toolArgs -join ' ')"
        Write-Host ''

        & $tool @toolArgs
        if ($LASTEXITCODE -ne 0) {
            throw "SEWorkshopTool exited with code $LASTEXITCODE. Check the log under %AppData%\SpaceEngineers\."
        }
    }
    catch {
        if ($bumped) {
            Write-Warning "Deploy failed; reverting modinfo version $newVersion -> $currentVersion"
            Set-ModVersion -Version $currentVersion
        }
        throw
    }

    # A first publish makes SEWorkshopTool write the new item's id into the STAGED modinfo.sbmi: keep it in the repo's
    if (-not $DryRun -and -not $updateOnly) {
        $stagedInfo = Join-Path $script:StageDir 'modinfo.sbmi'
        if (Test-Path -LiteralPath $stagedInfo) {
            Copy-Item -LiteralPath $stagedInfo -Destination $script:ModInfoPath -Force
            Write-Host 'Copied the new item id back into the mod folder''s modinfo.sbmi'
        }
    }

    Write-Host ''
    Write-Host 'Deploy finished successfully.' -ForegroundColor Green
}
