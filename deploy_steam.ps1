# Publish Automata to Steam Workshop via SEWorkshopTool.
# Requires Automata\modinfo.sbmi with the WorkshopId, and SEWorkshopTool next to Space Engineers.
# Pushes a clean copy of the shipped files prepared in .deploy\Automata (New-DeployStage in deploy_common.ps1); under
# Data\Scripts only the *.cs files (Remove-NonScriptFiles), without their comments (Remove-CodeComments).
#
# Usage:
#   .\deploy_steam.ps1
#   .\deploy_steam.ps1 -Bump minor -Message "Relay graph rework"
#   .\deploy_steam.ps1 -DryRun
#   .\deploy_steam.ps1 -Create        # first publish of a new item (no --update-only)
#
# Changelog: uses SEWorkshopTool push --message (Steam change notes; requires content upload).

[CmdletBinding()]
param(
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    [switch]$SkipVersionBump,
    [switch]$DryRun,
    [switch]$Force,
    [switch]$Create,
    [string]$Message
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\deploy_common.ps1"

Invoke-ModDeploy -Platform Steam @PSBoundParameters
