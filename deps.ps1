<#
.SYNOPSIS
    Checks what NorthCommander needs on this PC and installs missing parts, but only after asking.

.DESCRIPTION
    Works from every kind of NorthCommander folder, installed or portable:
      - full release (self-contained NorthCommander.exe): needs no .NET at all,
      - lite release (NorthCommander.exe plus NorthCommander.package.json): needs the .NET 10 runtime,
      - source clone: needs the .NET 10 SDK to build NorthCommander.
    Where missing parts can be installed (-Scope):
      App      .NET runtime into the 'dotnet' folder next to NorthCommander.exe. Portable: it moves with
               the folder, needs no administrator rights and writes nothing to the registry.
      User     .NET SDK into %LOCALAPPDATA%\Microsoft\dotnet (no administrator rights).
      Machine  system-wide with winget into Program Files (Windows asks for permission).
    Start it with Check-Dependencies.cmd; setup.ps1 and NorthCommander-Setup.exe use the same checks.

.PARAMETER Check
    Only report. Exit code 0 means everything is present, 1 means something is missing.

.PARAMETER Yes
    Install missing parts without asking (used by the setup programs). Combine with -Scope.

.PARAMETER Scope
    App or Machine for the runtime (default App), User or Machine for the SDK (default Machine
    when winget is available, otherwise User).

.PARAMETER Location
    The NorthCommander folder to check. Default: the folder of this script.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\deps.ps1 -Check
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Yes,
    [ValidateSet('', 'App', 'User', 'Machine')]
    [string]$Scope = '',
    [string]$Location = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$NcDotnetMajor = 10
$NcDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'
$NcInstallScriptUrl = 'https://dot.net/v1/dotnet-install.ps1'
# winget exit codes that mean "already installed / nothing to update".
$NcWingetOkCodes = @(0, -1978335189, -1978335135)

function Get-NcPackage([string]$Path) {
    <# Which kind of NorthCommander folder this is, and which .NET major version it needs. #>
    $manifest = Join-Path $Path 'NorthCommander.package.json'
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        $info = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
        $major = $NcDotnetMajor
        if ($info.PSObject.Properties['runtimeVersion'] -and "$($info.runtimeVersion)" -match '^(\d+)') { $major = [int]$Matches[1] }
        return [pscustomobject]@{ Kind = 'Lite'; Major = $major }
    }
    if (Test-Path -LiteralPath (Join-Path $Path 'NorthCommander.exe') -PathType Leaf) {
        return [pscustomobject]@{ Kind = 'Full'; Major = $NcDotnetMajor }
    }
    if (Test-Path -LiteralPath (Join-Path $Path 'src\NorthCommander.UI\NorthCommander.UI.csproj') -PathType Leaf) {
        return [pscustomobject]@{ Kind = 'Source'; Major = $NcDotnetMajor }
    }
    return [pscustomobject]@{ Kind = 'Unknown'; Major = $NcDotnetMajor }
}

function Get-NcVersionFolder([string]$Path, [int]$Major) {
    <# The newest "<Major>.x.y" sub folder of $Path, or $null. #>
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $null }
    Get-ChildItem -LiteralPath $Path -Directory |
        Where-Object { $_.Name -match "^$Major\.\d+\.\d+" } |
        Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
        Select-Object -First 1 -ExpandProperty Name
}

function Get-NcRuntime([string]$AppFolder, [int]$Major = $NcDotnetMajor) {
    <# Finds the .NET runtime where NorthCommander.exe looks for it: app folder, DOTNET_ROOT, system. #>
    $candidates = New-Object System.Collections.Generic.List[object]
    $candidates.Add(@('app folder', (Join-Path $AppFolder 'dotnet')))
    foreach ($name in 'DOTNET_ROOT_X64', 'DOTNET_ROOT') {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $candidates.Add(@($name, $value)) }
    }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64', 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64') {
        $registered = Get-ItemProperty -LiteralPath $key -Name InstallLocation -ErrorAction SilentlyContinue
        if ($registered) { $candidates.Add(@('system', $registered.InstallLocation)) }
    }
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    if ($programFiles) { $candidates.Add(@('system', (Join-Path $programFiles 'dotnet'))) }

    foreach ($candidate in $candidates) {
        $version = Get-NcVersionFolder -Path (Join-Path $candidate[1] 'shared\Microsoft.NETCore.App') -Major $Major
        if ($version) { return [pscustomobject]@{ Version = $version; Where = $candidate[0]; Root = $candidate[1] } }
    }
    return $null
}

function Get-NcSdk([int]$Major = $NcDotnetMajor) {
    <# Finds a dotnet.exe with a .NET $Major SDK; also lists every SDK version seen. #>
    $candidates = New-Object System.Collections.Generic.List[string]
    $onPath = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Source) }
    # A freshly installed SDK is not on this process's PATH yet.
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    if ($programFiles) { $candidates.Add((Join-Path $programFiles 'dotnet\dotnet.exe')) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) }

    $all = New-Object System.Collections.Generic.List[string]
    $found = $null
    foreach ($dotnet in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { continue }
        $sdkFolder = Join-Path (Split-Path -Parent $dotnet) 'sdk'
        if (Test-Path -LiteralPath $sdkFolder -PathType Container) {
            foreach ($dir in Get-ChildItem -LiteralPath $sdkFolder -Directory) {
                if ($dir.Name -match '^\d+\.\d+\.\d+' -and -not $all.Contains($dir.Name)) { $all.Add($dir.Name) }
            }
        }
        $version = Get-NcVersionFolder -Path $sdkFolder -Major $Major
        if ($version -and -not $found) { $found = [pscustomobject]@{ Dotnet = $dotnet; Version = $version } }
    }
    [pscustomobject]@{
        Dotnet  = if ($found) { $found.Dotnet } else { $null }
        Version = if ($found) { $found.Version } else { $null }
        All     = $all.ToArray()
    }
}

function Test-NcWinget {
    [bool](Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue)
}

function Get-NcStatus([string]$Path) {
    <# Everything the setup programs and this script show: one row per requirement, plus what is missing. #>
    $package = Get-NcPackage -Path $Path
    $rows = New-Object System.Collections.Generic.List[object]
    $os = [Environment]::OSVersion.Version
    $osOk = $os.Major -ge 10 -and [Environment]::Is64BitOperatingSystem
    $rows.Add([pscustomobject]@{ Name = 'Windows 10/11, 64-bit'; Ok = $osOk; Status = $(if ($osOk) { "OK (build $($os.Build))" } else { 'Not supported' }) })

    $missing = $null
    $runtime = $null
    $sdk = $null
    switch ($package.Kind) {
        'Full' {
            $rows.Add([pscustomobject]@{ Name = 'NorthCommander.exe (self-contained, no .NET needed)'; Ok = $true; Status = 'Found' })
        }
        'Lite' {
            $runtime = Get-NcRuntime -AppFolder $Path -Major $package.Major
            $name = ".NET $($package.Major) runtime (for NorthCommander Lite)"
            if ($runtime) { $rows.Add([pscustomobject]@{ Name = $name; Ok = $true; Status = "Found $($runtime.Version) ($($runtime.Where))" }) }
            else { $rows.Add([pscustomobject]@{ Name = $name; Ok = $false; Status = 'Missing' }); $missing = 'Runtime' }
        }
        'Source' {
            $sdk = Get-NcSdk -Major $package.Major
            $name = ".NET $($package.Major) SDK (to build from source)"
            if ($sdk.Version) { $rows.Add([pscustomobject]@{ Name = $name; Ok = $true; Status = "Found $($sdk.Version)" }) }
            else {
                $seen = if ($sdk.All.Count) { "Missing (found: $($sdk.All -join ', '))" } else { 'Missing' }
                $rows.Add([pscustomobject]@{ Name = $name; Ok = $false; Status = $seen }); $missing = 'Sdk'
            }
        }
        default {
            $rows.Add([pscustomobject]@{ Name = 'NorthCommander'; Ok = $false; Status = "Not found in $Path" })
        }
    }
    $winget = Test-NcWinget
    if ($package.Kind -in 'Lite', 'Source') {
        $rows.Add([pscustomobject]@{ Name = 'winget (to install system-wide)'; Ok = $true; Status = $(if ($winget) { 'Found' } else { 'Not found (optional)' }) })
    }
    [pscustomobject]@{
        Kind = $package.Kind; Major = $package.Major; Rows = $rows.ToArray(); Missing = $missing
        Runtime = $runtime; Sdk = $sdk; Winget = $winget; OsOk = $osOk
    }
}

function Get-NcDefaultScope([string]$Component) {
    if ($Component -eq 'Runtime') { return 'App' }
    if (Test-NcWinget) { 'Machine' } else { 'User' }
}

function Install-NcDependency([string]$Component, [string]$TargetScope, [string]$Path, [int]$Major = $NcDotnetMajor) {
    <# Installs the .NET runtime or SDK; throws when it fails. #>
    $label = if ($Component -eq 'Runtime') { ".NET $Major runtime" } else { ".NET $Major SDK" }
    $valid = if ($Component -eq 'Runtime') { 'App', 'Machine' } else { 'User', 'Machine' }
    if ($TargetScope -notin $valid) { throw "The $label can be installed with -Scope $($valid -join ' or '), not $TargetScope." }

    if ($TargetScope -eq 'Machine') {
        if (-not (Test-NcWinget)) {
            throw "winget isn't available, so the $label can't be installed system-wide here. Install it from $NcDownloadUrl" +
            $(if ($Component -eq 'Runtime') { ' or use -Scope App.' } else { ' or use -Scope User.' })
        }
        $id = if ($Component -eq 'Runtime') { "Microsoft.DotNet.Runtime.$Major" } else { "Microsoft.DotNet.SDK.$Major" }
        Write-Output "Installing the $label system-wide with winget ($id). Windows asks for permission..."
        & winget.exe install --id $id --exact --source winget --silent --accept-package-agreements --accept-source-agreements
        if ($NcWingetOkCodes -notcontains $LASTEXITCODE) { throw "winget failed with exit code $LASTEXITCODE." }
    }
    else {
        $installDir = if ($TargetScope -eq 'App') { Join-Path $Path 'dotnet' } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet' }
        Write-Output "Installing the $label into $installDir with Microsoft's dotnet-install.ps1 (no administrator rights needed)..."
        $script = Join-Path ([System.IO.Path]::GetTempPath()) "dotnet-install-$([guid]::NewGuid().ToString('N')).ps1"
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $NcInstallScriptUrl -OutFile $script -UseBasicParsing
            $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $script, '-Channel', "$Major.0", '-Architecture', 'x64', '-InstallDir', $installDir, '-NoPath')
            if ($Component -eq 'Runtime') { $arguments += @('-Runtime', 'dotnet') }
            & (Get-Process -Id $PID).Path @arguments
            if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed with exit code $LASTEXITCODE." }
        }
        finally {
            Remove-Item -LiteralPath $script -Force -ErrorAction SilentlyContinue
        }
        if ($Component -eq 'Sdk') {
            Write-Output "To build by hand, use `"$installDir\dotnet.exe`" (setup.ps1 finds it by itself)."
        }
    }

    $found = if ($Component -eq 'Runtime') { Get-NcRuntime -AppFolder $Path -Major $Major } else { (Get-NcSdk -Major $Major).Version }
    if (-not $found) { throw "The $label still isn't found after installing. Install it from $NcDownloadUrl" }
    Write-Output "The $label is installed."
}

function Invoke-NcDependencyCli([string]$Path, [bool]$CheckOnly, [bool]$NoPrompt, [string]$TargetScope) {
    if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
        Write-Output 'deps.ps1 checks NorthCommander dependencies on Windows and only runs on Windows.'
        $script:NcExitCode = 1; return
    }
    $Path = [System.IO.Path]::GetFullPath($Path)
    $status = Get-NcStatus -Path $Path
    Write-Output "NorthCommander dependencies in $Path"
    foreach ($row in $status.Rows) {
        Write-Output ('  [{0}] {1,-52} {2}' -f $(if ($row.Ok) { 'OK' } else { '!!' }), $row.Name, $row.Status)
    }
    if ($status.Kind -eq 'Unknown' -or -not $status.OsOk) { $script:NcExitCode = 1; return }
    if (-not $status.Missing) {
        Write-Output 'Everything NorthCommander needs is present.'
        $script:NcExitCode = 0; return
    }
    if ($CheckOnly) { $script:NcExitCode = 1; return }

    $component = $status.Missing
    $label = if ($component -eq 'Runtime') { ".NET $($status.Major) runtime" } else { ".NET $($status.Major) SDK" }
    if (-not $NoPrompt) {
        Write-Output ''
        if ($component -eq 'Runtime') {
            Write-Output "NorthCommander Lite needs the $label (download of about 30 MB). Install it:"
            Write-Output '  [A] into this app folder (portable: moves with the folder, no administrator rights)'
            Write-Output '  [M] system-wide with winget (Windows asks for permission)'
        }
        else {
            Write-Output "Building NorthCommander needs the $label (download of about 200 MB). Install it:"
            Write-Output '  [U] for your user only (%LOCALAPPDATA%\Microsoft\dotnet, no administrator rights)'
            Write-Output '  [M] system-wide with winget (Windows asks for permission)'
        }
        Write-Output '  [N] not now'
        $answer = (Read-Host 'Your choice').Trim().ToUpperInvariant()
        $TargetScope = switch ($answer) {
            'A' { if ($component -eq 'Runtime') { 'App' } else { '' } }
            'U' { if ($component -eq 'Sdk') { 'User' } else { '' } }
            'M' { 'Machine' }
            default { '' }
        }
        if (-not $TargetScope) {
            Write-Output "Nothing installed. Download page: $NcDownloadUrl"
            $script:NcExitCode = 1; return
        }
    }
    elseif (-not $TargetScope) {
        $TargetScope = Get-NcDefaultScope -Component $component
    }

    try {
        Install-NcDependency -Component $component -TargetScope $TargetScope -Path $Path -Major $status.Major
        $script:NcExitCode = 0; return
    }
    catch {
        Write-Output "ERROR: $($_.Exception.Message)"
        $script:NcExitCode = 1; return
    }
}

# When dot-sourced (setup.ps1 does that) only the functions above are defined.
if ($MyInvocation.InvocationName -ne '.') {
    # Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults, so resolve it here.
    if (-not $Location) { $Location = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path } }
    $script:NcExitCode = 1
    Invoke-NcDependencyCli -Path $Location -CheckOnly $Check.IsPresent -NoPrompt $Yes.IsPresent -TargetScope $Scope
    exit $script:NcExitCode
}
