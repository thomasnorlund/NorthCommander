<#
.SYNOPSIS
    Adds NorthCommander to the current user's Start Menu (and optionally the Desktop).

.DESCRIPTION
    NorthCommander stays portable: nothing is copied, no registry keys are written and no
    administrator rights are needed. The script creates shortcuts to the NorthCommander.exe
    in the folder this script lives in. Run uninstall.ps1 to remove them again.

.PARAMETER Desktop
    Also create a Desktop shortcut without asking.

.PARAMETER NoDesktop
    Do not create a Desktop shortcut and do not ask.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [switch]$Desktop,
    [switch]$NoDesktop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Error 'install.ps1 creates Windows shortcuts and only runs on Windows.'
    exit 1
}

$appFolder = $PSScriptRoot
$exePath = Join-Path $appFolder 'NorthCommander.exe'
if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    Write-Error "NorthCommander.exe was not found in '$appFolder'. Keep install.ps1 in the same folder as NorthCommander.exe."
    exit 1
}

$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NorthCommander.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'NorthCommander.lnk'

function New-NorthCommanderShortcut {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$Path)

    if (-not $PSCmdlet.ShouldProcess($Path, 'Create shortcut')) {
        return
    }

    $folder = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        $shortcut.TargetPath = $exePath
        $shortcut.WorkingDirectory = $appFolder
        $shortcut.IconLocation = "$exePath,0"
        $shortcut.Description = 'NorthCommander - dual-pane file manager'
        $shortcut.Save()
    }
    finally {
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }

    Write-Output "Created $Path"
}

# Files from a downloaded zip carry the "downloaded from the internet" mark; clear it for this folder only.
Get-ChildItem -LiteralPath $appFolder -Recurse -File | Unblock-File

New-NorthCommanderShortcut -Path $startMenuShortcut

$createDesktop = $Desktop.IsPresent
if (-not $Desktop -and -not $NoDesktop) {
    try {
        $answer = Read-Host 'Also create a Desktop shortcut? [y/N]'
        $createDesktop = $answer -match '^(y|yes|j|ja)$'
    }
    catch {
        # Non-interactive session: no Desktop shortcut unless -Desktop is given.
        $createDesktop = $false
    }
}

if ($createDesktop) {
    New-NorthCommanderShortcut -Path $desktopShortcut
}

Write-Output ''
Write-Output 'Done. Type "North" in Windows Search or the Start Menu to launch NorthCommander.'
Write-Output 'If you move the NorthCommander folder, run install.ps1 again from the new location.'
