<#
.SYNOPSIS
    Integrates portable NorthCommander into Windows for the current user.

.DESCRIPTION
    Uses the NorthCommander.exe in the folder this script lives in. Nothing is copied and no
    administrator rights are needed. The script:
      1. creates NorthCommander.lnk in the Start Menu and on the Desktop (setup.ps1 / Setup.cmd
         offers the same steps in a window),
      2. adds this folder to the user PATH, so "northcommander" or "north" (north.cmd)
         starts NorthCommander from any new terminal.
    NorthCommander itself keeps all settings in NorthCommander.config.json next to the exe.
    The PATH entry is the only registry change (HKCU\Environment, where Windows stores the
    user PATH). Run uninstall.ps1 to undo everything.

.PARAMETER NoStartMenu
    Skip the Start Menu shortcut.

.PARAMETER NoDesktop
    Skip the Desktop shortcut.

.PARAMETER NoPath
    Do not add the folder to the user PATH.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$NoStartMenu,
    [switch]$NoDesktop,
    [switch]$NoPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Error 'install.ps1 integrates NorthCommander into Windows and only runs on Windows.'
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

    Write-Output "Created shortcut $Path"
}

function Test-SamePath([string]$A, [string]$B) {
    $A.Trim().TrimEnd('\') -ieq $B.Trim().TrimEnd('\')
}

function Add-NorthCommanderToUserPath {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    # Read the raw value so entries like %USERPROFILE%\bin stay unexpanded.
    $key = Get-Item -LiteralPath 'HKCU:\Environment'
    $current = [string]$key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    $entries = @($current -split ';' | Where-Object { $_.Trim() })

    if ($entries | Where-Object { Test-SamePath ([Environment]::ExpandEnvironmentVariables($_)) $appFolder }) {
        Write-Output "User PATH already contains $appFolder"
    }
    elseif ($PSCmdlet.ShouldProcess('User PATH', "Add $appFolder")) {
        $newPath = (@($entries) + $appFolder) -join ';'
        $kind = if ($newPath.Contains('%')) { 'ExpandString' } else { 'String' }
        if ($kind -eq 'String') {
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
        }
        else {
            # SetEnvironmentVariable would store REG_SZ and break %VAR% entries; keep REG_EXPAND_SZ.
            Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name 'Path' -Value $newPath -Type ExpandString
            # Setting and clearing a helper variable broadcasts WM_SETTINGCHANGE so new terminals see the change.
            [Environment]::SetEnvironmentVariable('NorthCommanderInstall', '1', 'User')
            [Environment]::SetEnvironmentVariable('NorthCommanderInstall', $null, 'User')
        }
        Write-Output "Added $appFolder to the user PATH"
    }

    # Make the commands work in this window too.
    if (-not (($env:Path -split ';') | Where-Object { Test-SamePath $_ $appFolder })) {
        $env:Path = "$env:Path;$appFolder"
    }
}

# Files from a downloaded zip carry the "downloaded from the internet" mark; clear it for this folder only.
Get-ChildItem -LiteralPath $appFolder -Recurse -File | Unblock-File

if (-not $NoStartMenu) {
    New-NorthCommanderShortcut -Path $startMenuShortcut
}
if (-not $NoDesktop) {
    New-NorthCommanderShortcut -Path $desktopShortcut
}
if (-not $NoPath) {
    Add-NorthCommanderToUserPath
}

Write-Output ''
Write-Output 'Done. Start NorthCommander from the Start Menu ("North" in Windows Search), the Desktop,'
Write-Output 'or by typing "north" or "northcommander" in a new terminal.'
Write-Output 'If you move the NorthCommander folder, run uninstall.ps1 first and install.ps1 again from the new location.'
