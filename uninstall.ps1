<#
.SYNOPSIS
    Removes the Start Menu and Desktop shortcuts created by install.ps1.

.DESCRIPTION
    Only the two NorthCommander.lnk files are deleted. The NorthCommander folder and its
    settings file (NorthCommander.config.json) are left untouched; delete the folder to
    remove NorthCommander completely. No administrator rights are needed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Error 'uninstall.ps1 removes Windows shortcuts and only runs on Windows.'
    exit 1
}

$shortcuts = @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\NorthCommander.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'NorthCommander.lnk')
)

foreach ($shortcut in $shortcuts) {
    if (Test-Path -LiteralPath $shortcut -PathType Leaf) {
        Remove-Item -LiteralPath $shortcut -Force
        Write-Output "Removed $shortcut"
    }
    else {
        Write-Output "Not found (nothing to do): $shortcut"
    }
}
