<#
.SYNOPSIS
    Graphical setup for NorthCommander (start it with Setup.cmd).

.DESCRIPTION
    A small Windows Forms wizard that runs on the Windows PowerShell 5.1 built into Windows 10/11:
      1. checks the requirements and lists what is missing,
      2. installs missing parts with winget, but only after asking (Windows shows its own
         permission prompt for installers that need it),
      3. from a source clone: builds a self-contained NorthCommander.exe into the chosen folder;
         from a release zip: uses the NorthCommander.exe next to this script (copied if you
         choose another folder),
      4. runs install.ps1 in the app folder for the Start Menu / Desktop shortcuts and PATH.
    The installed app needs no .NET runtime and stays portable: everything lives in its folder.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -STA -File .\setup.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Error 'setup.ps1 sets up NorthCommander on Windows and only runs on Windows.'
    exit 1
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$sourceRoot = $PSScriptRoot
$projectPath = Join-Path $sourceRoot 'src\NorthCommander.UI\NorthCommander.UI.csproj'
$isRelease = Test-Path -LiteralPath (Join-Path $sourceRoot 'NorthCommander.exe') -PathType Leaf
$isSource = (-not $isRelease) -and (Test-Path -LiteralPath $projectPath -PathType Leaf)
$requiredSdkMajor = 10
$sdkWingetId = 'Microsoft.DotNet.SDK.10'
$sdkDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0'
$supportFiles = @('install.ps1', 'uninstall.ps1', 'north.cmd', 'README.md', 'LICENSE')
# winget exit codes that mean "already installed / nothing to update".
$wingetOkCodes = @(0, -1978335189, -1978335135)
$oemEncoding = [System.Text.Encoding]::GetEncoding([System.Globalization.CultureInfo]::CurrentCulture.TextInfo.OEMCodePage)

if (-not $isRelease -and -not $isSource) {
    [void][System.Windows.Forms.MessageBox]::Show(
        "Neither NorthCommander.exe nor the NorthCommander source code was found next to setup.ps1 in`n$sourceRoot",
        'NorthCommander Setup', 'OK', 'Error')
    exit 1
}

# ---------------------------------------------------------------- requirement checks

function Get-DotnetSdk {
    <# Returns the dotnet.exe that has a .NET $requiredSdkMajor SDK, plus every SDK version seen. #>
    $ErrorActionPreference = 'Continue'
    $candidates = New-Object System.Collections.Generic.List[string]
    $onPath = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $candidates.Add($onPath.Source) }
    # A freshly installed SDK is not on this process's PATH yet.
    if ($env:ProgramFiles) { $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) }

    $all = New-Object System.Collections.Generic.List[string]
    foreach ($dotnet in ($candidates | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { continue }
        $match = $null
        try { $lines = & $dotnet --list-sdks 2>$null } catch { $lines = @() }
        foreach ($line in @($lines)) {
            if ("$line" -match '^(\d+)\.\S+') {
                if (-not $all.Contains($Matches[0])) { $all.Add($Matches[0]) }
                if ([int]$Matches[1] -eq $requiredSdkMajor) { $match = $Matches[0] }
            }
        }
        if ($match) {
            return [pscustomobject]@{ Dotnet = $dotnet; Version = $match; All = @($all) }
        }
    }
    return [pscustomobject]@{ Dotnet = $null; Version = $null; All = @($all) }
}

function Test-Winget {
    [bool](Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue)
}

$script:sdk = $null

function Show-Requirement {
    $requirements.Items.Clear()
    $os = [Environment]::OSVersion.Version
    $osOk = $os.Major -ge 10 -and [Environment]::Is64BitOperatingSystem
    Add-RequirementRow -Name 'Windows 10/11, 64-bit' -Ok $osOk -Status $(if ($osOk) { "OK (build $($os.Build))" } else { 'Not supported' })

    if ($isRelease) {
        Add-RequirementRow -Name 'NorthCommander.exe (self-contained, no .NET needed)' -Ok $true -Status 'Found'
        $installMissing.Enabled = $false
        return
    }

    $script:sdk = Get-DotnetSdk
    if ($script:sdk.Version) {
        Add-RequirementRow -Name ".NET $requiredSdkMajor SDK (to build from source)" -Ok $true -Status "Found $($script:sdk.Version)"
    }
    else {
        $found = if ($script:sdk.All.Count) { "Missing (found: $($script:sdk.All -join ', '))" } else { 'Missing' }
        Add-RequirementRow -Name ".NET $requiredSdkMajor SDK (to build from source)" -Ok $false -Status $found
    }
    $hasWinget = Test-Winget
    Add-RequirementRow -Name 'winget (to install missing parts)' -Ok $true -Status $(if ($hasWinget) { 'Found' } else { 'Not found (optional)' })
    $installMissing.Enabled = -not $script:sdk.Version
}

function Add-RequirementRow([string]$Name, [bool]$Ok, [string]$Status) {
    $item = New-Object System.Windows.Forms.ListViewItem($Name)
    [void]$item.SubItems.Add($Status)
    $item.ForeColor = if ($Ok) { [System.Drawing.Color]::DarkGreen } else { [System.Drawing.Color]::Firebrick }
    [void]$requirements.Items.Add($item)
}

# ---------------------------------------------------------------- step runner (keeps the window responsive)

$script:queue = New-Object System.Collections.Queue
$script:proc = $null
$script:outTask = $null
$script:errTask = $null
$script:step = $null
$script:onSuccess = $null
$script:busy = $false

function Write-SetupLog([string]$Text) {
    if ($Text -match '^\s*[-\\|/]?\s*$') { return }
    if ($Text.IndexOf([char]0x2588) -ge 0 -or $Text.IndexOf([char]0x2592) -ge 0) { return }
    $log.AppendText($Text.TrimEnd() + "`r`n")
}

function Get-ProcessStep([string]$Title, [string]$File, [string]$Arguments, [string]$WorkingDirectory, [int[]]$OkCodes = @(0), $Encoding = $oemEncoding) {
    [pscustomobject]@{ Title = $Title; File = $File; Arguments = $Arguments; WorkingDirectory = $WorkingDirectory; OkCodes = $OkCodes; Encoding = $Encoding; Action = $null }
}

function Get-ActionStep([string]$Title, [scriptblock]$Action) {
    [pscustomobject]@{ Title = $Title; File = $null; Arguments = $null; WorkingDirectory = $null; OkCodes = @(0); Encoding = $null; Action = $Action }
}

function Invoke-Step([object[]]$Steps, [scriptblock]$OnSuccess) {
    $script:queue.Clear()
    foreach ($s in $Steps) { $script:queue.Enqueue($s) }
    $script:onSuccess = $OnSuccess
    $script:busy = $true
    foreach ($c in $lockedControls) { $c.Enabled = $false }
    $progress.Style = 'Marquee'
    Invoke-NextStep
}

function Invoke-NextStep {
    while ($script:queue.Count -gt 0) {
        $script:step = $script:queue.Dequeue()
        Write-SetupLog "== $($script:step.Title)"
        if ($script:step.Action) {
            try { & $script:step.Action }
            catch {
                Write-SetupLog "ERROR: $($_.Exception.Message)"
                Complete-Step $false
                return
            }
            continue
        }

        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $script:step.File
        $psi.Arguments = $script:step.Arguments
        if ($script:step.WorkingDirectory) { $psi.WorkingDirectory = $script:step.WorkingDirectory }
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.StandardOutputEncoding = $script:step.Encoding
        $psi.StandardErrorEncoding = $script:step.Encoding
        $psi.EnvironmentVariables['DOTNET_NOLOGO'] = '1'
        try { $script:proc = [System.Diagnostics.Process]::Start($psi) }
        catch {
            Write-SetupLog "ERROR: could not start $($script:step.File): $($_.Exception.Message)"
            Complete-Step $false
            return
        }
        $script:outTask = $script:proc.StandardOutput.ReadLineAsync()
        $script:errTask = $script:proc.StandardError.ReadLineAsync()
        $timer.Start()
        return
    }
    Complete-Step $true
}

function Read-ProcessOutput {
    for ($i = 0; $i -lt 500 -and $script:outTask -and $script:outTask.IsCompleted; $i++) {
        $line = $script:outTask.Result
        if ($null -eq $line) { $script:outTask = $null } else { Write-SetupLog $line; $script:outTask = $script:proc.StandardOutput.ReadLineAsync() }
    }
    for ($i = 0; $i -lt 500 -and $script:errTask -and $script:errTask.IsCompleted; $i++) {
        $line = $script:errTask.Result
        if ($null -eq $line) { $script:errTask = $null } else { Write-SetupLog $line; $script:errTask = $script:proc.StandardError.ReadLineAsync() }
    }
}

function Complete-Step([bool]$Ok) {
    $timer.Stop()
    $script:busy = $false
    $progress.Style = 'Blocks'
    $progress.Value = if ($Ok) { 100 } else { 0 }
    foreach ($c in $lockedControls) { $c.Enabled = $true }
    Show-Requirement
    if ($Ok) {
        if ($script:onSuccess) { & $script:onSuccess }
    }
    else {
        [void][System.Windows.Forms.MessageBox]::Show('A step failed. See the log for details.', 'NorthCommander Setup', 'OK', 'Error')
    }
}

# ---------------------------------------------------------------- window

$form = New-Object System.Windows.Forms.Form
$form.Text = 'NorthCommander Setup'
$form.ClientSize = New-Object System.Drawing.Size(660, 600)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9)

$header = New-Object System.Windows.Forms.Panel
$header.SetBounds(0, 0, 660, 64)
$header.BackColor = [System.Drawing.Color]::FromArgb(0, 0, 168)
$form.Controls.Add($header)

$title = New-Object System.Windows.Forms.Label
$title.Text = 'NorthCommander Setup'
$title.Font = New-Object System.Drawing.Font('Consolas', 16, [System.Drawing.FontStyle]::Bold)
$title.ForeColor = [System.Drawing.Color]::FromArgb(255, 255, 85)
$title.SetBounds(16, 8, 620, 28)
$header.Controls.Add($title)

$subtitle = New-Object System.Windows.Forms.Label
$subtitle.Text = if ($isRelease) { 'Release package: no dependencies needed. Choose where NorthCommander should live.' } else { 'Source code: setup checks the build tools, builds NorthCommander.exe and sets it up.' }
$subtitle.ForeColor = [System.Drawing.Color]::FromArgb(85, 255, 255)
$subtitle.SetBounds(16, 38, 620, 20)
$header.Controls.Add($subtitle)

$reqGroup = New-Object System.Windows.Forms.GroupBox
$reqGroup.Text = '1. Requirements'
$reqGroup.SetBounds(12, 74, 636, 150)
$form.Controls.Add($reqGroup)

$requirements = New-Object System.Windows.Forms.ListView
$requirements.View = 'Details'
$requirements.FullRowSelect = $true
$requirements.HeaderStyle = 'Nonclickable'
$requirements.SetBounds(12, 22, 612, 82)
[void]$requirements.Columns.Add('Requirement', 360)
[void]$requirements.Columns.Add('Status', 230)
$reqGroup.Controls.Add($requirements)

$installMissing = New-Object System.Windows.Forms.Button
$installMissing.Text = 'Install missing...'
$installMissing.SetBounds(374, 112, 130, 28)
$reqGroup.Controls.Add($installMissing)

$recheck = New-Object System.Windows.Forms.Button
$recheck.Text = 'Check again'
$recheck.SetBounds(510, 112, 114, 28)
$reqGroup.Controls.Add($recheck)

$setupGroup = New-Object System.Windows.Forms.GroupBox
$setupGroup.Text = '2. Set up'
$setupGroup.SetBounds(12, 232, 636, 150)
$form.Controls.Add($setupGroup)

$folderLabel = New-Object System.Windows.Forms.Label
$folderLabel.Text = 'App folder (settings are kept here too):'
$folderLabel.SetBounds(12, 22, 400, 18)
$setupGroup.Controls.Add($folderLabel)

$folder = New-Object System.Windows.Forms.TextBox
$folder.Text = if ($isRelease) { $sourceRoot } else { Join-Path $env:LOCALAPPDATA 'Programs\NorthCommander' }
$folder.SetBounds(12, 42, 516, 24)
$setupGroup.Controls.Add($folder)

$browse = New-Object System.Windows.Forms.Button
$browse.Text = 'Browse...'
$browse.SetBounds(534, 41, 90, 26)
$setupGroup.Controls.Add($browse)

function Add-SetupCheckBox([string]$Text, [int]$X, [int]$Y, [int]$Width) {
    $box = New-Object System.Windows.Forms.CheckBox
    $box.Text = $Text
    $box.Checked = $true
    $box.SetBounds($X, $Y, $Width, 22)
    $setupGroup.Controls.Add($box)
    $box
}
$startMenu = Add-SetupCheckBox -Text 'Start Menu shortcut (find it by typing "North")' -X 12 -Y 76 -Width 330
$desktop = Add-SetupCheckBox -Text 'Desktop shortcut' -X 350 -Y 76 -Width 270
$pathBox = Add-SetupCheckBox -Text 'Add to PATH ("north" / "northcommander" in terminals)' -X 12 -Y 100 -Width 330
$launch = Add-SetupCheckBox -Text 'Start NorthCommander when done' -X 350 -Y 100 -Width 270

$progress = New-Object System.Windows.Forms.ProgressBar
$progress.SetBounds(12, 392, 636, 16)
$progress.MarqueeAnimationSpeed = 30
$form.Controls.Add($progress)

$log = New-Object System.Windows.Forms.TextBox
$log.Multiline = $true
$log.ReadOnly = $true
$log.ScrollBars = 'Vertical'
$log.WordWrap = $true
$log.Font = New-Object System.Drawing.Font('Consolas', 8.5)
$log.BackColor = [System.Drawing.Color]::FromArgb(0, 0, 168)
$log.ForeColor = [System.Drawing.Color]::FromArgb(85, 255, 255)
$log.SetBounds(12, 416, 636, 136)
$form.Controls.Add($log)

$install = New-Object System.Windows.Forms.Button
$install.Text = 'Install'
$install.SetBounds(452, 562, 96, 28)
$form.Controls.Add($install)
$form.AcceptButton = $install

$close = New-Object System.Windows.Forms.Button
$close.Text = 'Close'
$close.SetBounds(552, 562, 96, 28)
$close.Add_Click({ $form.Close() })
$form.Controls.Add($close)

$lockedControls = @($installMissing, $recheck, $folder, $browse, $startMenu, $desktop, $pathBox, $launch, $install)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 200
$timer.Add_Tick({
        Read-ProcessOutput
        if ($script:proc -and $script:proc.HasExited -and -not $script:outTask -and -not $script:errTask) {
            $timer.Stop()
            $code = $script:proc.ExitCode
            $script:proc.Dispose()
            $script:proc = $null
            if ($script:step.OkCodes -contains $code) {
                Invoke-NextStep
            }
            else {
                Write-SetupLog "ERROR: '$($script:step.Title)' failed with exit code $code."
                Complete-Step $false
            }
        }
    })

# ---------------------------------------------------------------- actions

$recheck.Add_Click({ Show-Requirement })

$browse.Add_Click({
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'Choose the NorthCommander app folder'
        $dialog.ShowNewFolderButton = $true
        if (Test-Path -LiteralPath $folder.Text) { $dialog.SelectedPath = $folder.Text }
        if ($dialog.ShowDialog($form) -eq 'OK') { $folder.Text = $dialog.SelectedPath }
    })

$installMissing.Add_Click({
        if (-not (Test-Winget)) {
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "winget (Microsoft's package manager) isn't available, so setup can't install the .NET $requiredSdkMajor SDK itself.`n`nOpen the download page? Install the SDK (x64), then click 'Check again'.",
                'NorthCommander Setup', 'YesNo', 'Question')
            if ($answer -eq 'Yes') { Start-Process $sdkDownloadUrl }
            return
        }
        $answer = [System.Windows.Forms.MessageBox]::Show(
            "Setup will install the .NET $requiredSdkMajor SDK from Microsoft using winget (download of roughly 200 MB). It is only needed to build NorthCommander; your other .NET versions stay installed.`n`nWindows will ask for permission, because the SDK installs into Program Files.`n`nInstall it now?",
            'NorthCommander Setup', 'YesNo', 'Question')
        if ($answer -ne 'Yes') { return }

        $steps = @(
            (Get-ProcessStep -Title "Installing .NET $requiredSdkMajor SDK with winget (accept the Windows prompt)" -File 'winget.exe' -Arguments "install --id $sdkWingetId --exact --source winget --silent --accept-package-agreements --accept-source-agreements" -OkCodes $wingetOkCodes -Encoding ([System.Text.Encoding]::UTF8)),
            (Get-ActionStep 'Checking the SDK' {
                $found = Get-DotnetSdk
                if (-not $found.Version) { throw ".NET $requiredSdkMajor SDK still not found. Install it from $sdkDownloadUrl and click 'Check again'." }
                Write-SetupLog "Found .NET SDK $($found.Version)"
            })
        )
        Invoke-Step $steps { Write-SetupLog 'Requirements are complete. Click Install to continue.' }
    })

$install.Add_Click({
        $text = [Environment]::ExpandEnvironmentVariables($folder.Text.Trim().Trim('"'))
        if (-not $text) { [void][System.Windows.Forms.MessageBox]::Show('Choose an app folder first.', 'NorthCommander Setup'); return }
        try { $target = [System.IO.Path]::GetFullPath($text).TrimEnd('\') }
        catch { [void][System.Windows.Forms.MessageBox]::Show("Invalid folder: $text", 'NorthCommander Setup', 'OK', 'Error'); return }
        if ($target.Length -le 2 -or -not [System.IO.Path]::IsPathRooted($target)) {
            [void][System.Windows.Forms.MessageBox]::Show('Choose a folder, not a drive root.', 'NorthCommander Setup', 'OK', 'Warning'); return
        }
        $source = $sourceRoot.TrimEnd('\')
        $sameFolder = $target -ieq $source
        if ($isSource -and $sameFolder) {
            [void][System.Windows.Forms.MessageBox]::Show('Choose an app folder other than the source code folder.', 'NorthCommander Setup', 'OK', 'Warning'); return
        }
        if ($isRelease -and -not $sameFolder -and $target.StartsWith("$source\", [StringComparison]::OrdinalIgnoreCase)) {
            [void][System.Windows.Forms.MessageBox]::Show('The app folder cannot be inside the package folder.', 'NorthCommander Setup', 'OK', 'Warning'); return
        }
        if ($isSource -and -not $script:sdk.Version) {
            [void][System.Windows.Forms.MessageBox]::Show("The .NET $requiredSdkMajor SDK is needed to build NorthCommander. Click 'Install missing...' first.", 'NorthCommander Setup', 'OK', 'Warning'); return
        }
        $exe = Join-Path $target 'NorthCommander.exe'
        $running = @(Get-Process -Name NorthCommander -ErrorAction SilentlyContinue | Where-Object {
                try { $_.Path -ieq $exe } catch { $false }
            })
        if ($running.Count) {
            [void][System.Windows.Forms.MessageBox]::Show('NorthCommander is running from that folder. Close it first.', 'NorthCommander Setup', 'OK', 'Warning'); return
        }

        # Steps run after this handler returns, so they read their inputs from script scope.
        $script:target = $target
        $script:exe = $exe
        $steps = New-Object System.Collections.Generic.List[object]
        $steps.Add((Get-ActionStep "Preparing $script:target" { if (-not (Test-Path -LiteralPath $script:target)) { New-Item -ItemType Directory -Path $script:target -Force | Out-Null } }))
        if ($isSource) {
            $publishArgs = "publish `"$projectPath`" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true " +
            "-p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:DebugType=None -o `"$script:target`""
            $steps.Add((Get-ProcessStep -Title 'Building NorthCommander.exe (the first build downloads packages and takes a few minutes)' -File $script:sdk.Dotnet -Arguments $publishArgs -WorkingDirectory $sourceRoot))
            $steps.Add((Get-ActionStep 'Copying scripts and docs' {
                        foreach ($name in $supportFiles) {
                            $file = Join-Path $sourceRoot $name
                            if (Test-Path -LiteralPath $file -PathType Leaf) { Copy-Item -LiteralPath $file -Destination $script:target -Force }
                        }
                    }))
        }
        elseif (-not $sameFolder) {
            $steps.Add((Get-ActionStep "Copying NorthCommander to $script:target" {
                        Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $script:target -Recurse -Force
                    }))
        }

        $installArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $script:target 'install.ps1')`""
        if (-not $startMenu.Checked) { $installArgs += ' -NoStartMenu' }
        if (-not $desktop.Checked) { $installArgs += ' -NoDesktop' }
        if (-not $pathBox.Checked) { $installArgs += ' -NoPath' }
        $powershell = (Get-Process -Id $PID).Path
        $steps.Add((Get-ProcessStep -Title 'Creating shortcuts and PATH entry (install.ps1)' -File $powershell -Arguments $installArgs -WorkingDirectory $script:target))

        $script:startApp = $launch.Checked
        Invoke-Step $steps.ToArray() {
            Write-SetupLog "Done. NorthCommander is set up in $script:target"
            if ($script:startApp) { Start-Process -FilePath $script:exe -WorkingDirectory $script:target }
            [void][System.Windows.Forms.MessageBox]::Show("NorthCommander is set up in`n$script:target`n`nTo remove the shortcuts and PATH entry later, run uninstall.ps1 in that folder.", 'NorthCommander Setup', 'OK', 'Information')
        }
    })

$form.Add_FormClosing({
        $closingArgs = $_
        if ($script:busy) {
            $answer = [System.Windows.Forms.MessageBox]::Show('A step is still running. Close setup anyway?', 'NorthCommander Setup', 'YesNo', 'Warning')
            if ($answer -ne 'Yes') { $closingArgs.Cancel = $true }
        }
    })

$form.Add_Shown({
        Show-Requirement
        if ($isSource -and -not $script:sdk.Version) {
            Write-SetupLog "The .NET $requiredSdkMajor SDK is missing. Click 'Install missing...' to install it."
        }
        else {
            Write-SetupLog 'Ready. Choose the app folder and options, then click Install.'
        }
    })

[void]$form.ShowDialog()
