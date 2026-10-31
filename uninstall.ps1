<#
.SYNOPSIS
    Undoes install.ps1: removes the NorthCommander shortcuts and the user PATH entry.

.DESCRIPTION
    Deletes NorthCommander.lnk from the Start Menu and Desktop and removes this folder from
    the user PATH. The NorthCommander folder and its settings file (NorthCommander.config.json)
    are left untouched; delete the folder to remove NorthCommander completely.
    No administrator rights are needed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
[CmdletBinding(SupportsShouldProcess)]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Error 'uninstall.ps1 removes the Windows integration and only runs on Windows.'
    exit 1
}

$appFolder = $PSScriptRoot
$shortcuts = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NorthCommander.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'NorthCommander.lnk')
)

foreach ($shortcut in $shortcuts) {
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
        if ($PSCmdlet.ShouldProcess($shortcut, 'Delete shortcut')) {
            Remove-Item -LiteralPath $shortcut -Force
            Write-Output "Removed $shortcut"
        }
    }
    else {
        Write-Output "Not found (nothing to do): $shortcut"
    }
}

function Test-SamePath([string]$A, [string]$B) {
    $A.Trim().TrimEnd('\') -ieq $B.Trim().TrimEnd('\')
}

$key = Get-Item -LiteralPath 'HKCU:\Environment'
$current = [string]$key.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
$entries = @($current -split ';' | Where-Object { $_.Trim() })
$remaining = @($entries | Where-Object { -not (Test-SamePath ([Environment]::ExpandEnvironmentVariables($_)) $appFolder) })

if ($remaining.Count -eq $entries.Count) {
    Write-Output "User PATH does not contain $appFolder (nothing to do)"
}
elseif ($PSCmdlet.ShouldProcess('User PATH', "Remove $appFolder")) {
    $newPath = $remaining -join ';'
    if ($newPath.Contains('%')) {
        Set-ItemProperty -LiteralPath 'HKCU:\Environment' -Name 'Path' -Value $newPath -Type ExpandString
        # Re-setting an unrelated variable broadcasts WM_SETTINGCHANGE so new terminals see the change.
        [Environment]::SetEnvironmentVariable('NorthCommanderUninstall', '1', 'User')
        [Environment]::SetEnvironmentVariable('NorthCommanderUninstall', $null, 'User')
    }
    else {
        [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    }
    Write-Output "Removed $appFolder from the user PATH"
}
