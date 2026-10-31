using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NorthCommander.Setup;

/// <summary>
/// The setup window: checks requirements, unpacks the embedded app into the chosen folder and runs
/// install.ps1 there for the Start Menu / Desktop shortcuts and the PATH entry.
/// </summary>
internal sealed class SetupForm : Form
{
    private const string Title = "NorthCommander Setup";
    private const string PayloadResource = "payload.zip";
    private const string AppExe = "NorthCommander.exe";

    private static readonly Color RetroBlue = Color.FromArgb(0, 0, 168);
    private static readonly Color RetroYellow = Color.FromArgb(255, 255, 85);
    private static readonly Color RetroCyan = Color.FromArgb(85, 255, 255);

    private readonly ListView _requirements = new();
    private readonly TextBox _folder = new();
    private readonly Button _browse = new();
    private readonly CheckBox _startMenu;
    private readonly CheckBox _desktop;
    private readonly CheckBox _path;
    private readonly CheckBox _launch;
    private readonly ProgressBar _progress = new();
    private readonly TextBox _log = new();
    private readonly Button _install = new();
    private readonly Button _close = new();
    private readonly Control[] _lockedControls;
    private readonly string _version;
    private readonly string _powerShell;
    private bool _busy;
    private bool _ready;

    public SetupForm()
    {
        _version = typeof(SetupForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
        _powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

        Text = Title;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(660, 560);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

        var header = new Panel { Bounds = new Rectangle(0, 0, 660, 64), BackColor = RetroBlue };
        header.Controls.Add(new Label
        {
            Text = Title,
            Font = new Font("Consolas", 16F, FontStyle.Bold),
            ForeColor = RetroYellow,
            Bounds = new Rectangle(16, 8, 620, 28),
        });
        header.Controls.Add(new Label
        {
            Text = $"Version {_version}. Installs for the current user only; no administrator rights needed.",
            ForeColor = RetroCyan,
            Bounds = new Rectangle(16, 38, 620, 20),
        });
        Controls.Add(header);

        var requirementsGroup = new GroupBox { Text = "1. Requirements", Bounds = new Rectangle(12, 74, 636, 112) };
        _requirements.View = View.Details;
        _requirements.FullRowSelect = true;
        _requirements.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _requirements.Bounds = new Rectangle(12, 22, 612, 80);
        requirementsGroup.Controls.Add(_requirements);
        Controls.Add(requirementsGroup);

        var setupGroup = new GroupBox { Text = "2. Set up", Bounds = new Rectangle(12, 194, 636, 136) };
        setupGroup.Controls.Add(new Label { Text = "App folder (settings are kept here too):", Bounds = new Rectangle(12, 22, 400, 18) });
        _folder.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NorthCommander");
        _folder.Bounds = new Rectangle(12, 42, 516, 24);
        setupGroup.Controls.Add(_folder);
        _browse.Text = "Browse...";
        _browse.Bounds = new Rectangle(534, 41, 90, 26);
        _browse.Click += OnBrowse;
        setupGroup.Controls.Add(_browse);
        _startMenu = AddCheckBox(setupGroup, "Start Menu shortcut (find it by typing \"North\")", 12, 76, 330);
        _desktop = AddCheckBox(setupGroup, "Desktop shortcut", 350, 76, 270);
        _path = AddCheckBox(setupGroup, "Add to PATH (type \"north\" in a terminal)", 12, 100, 330);
        _launch = AddCheckBox(setupGroup, "Start NorthCommander when done", 350, 100, 270);
        Controls.Add(setupGroup);

        _progress.Bounds = new Rectangle(12, 340, 636, 16);
        Controls.Add(_progress);

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font("Consolas", 8.5F);
        _log.BackColor = RetroBlue;
        _log.ForeColor = RetroCyan;
        _log.Bounds = new Rectangle(12, 364, 636, 148);
        Controls.Add(_log);

        _install.Text = "Install";
        _install.Bounds = new Rectangle(452, 522, 96, 28);
        _install.Click += OnInstall;
        Controls.Add(_install);
        AcceptButton = _install;

        _close.Text = "Close";
        _close.Bounds = new Rectangle(552, 522, 96, 28);
        _close.Click += (_, _) => Close();
        Controls.Add(_close);

        _lockedControls = [_folder, _browse, _startMenu, _desktop, _path, _launch, _install];

        // Layout is in 96-DPI pixels; scale it to the screen (fonts in points scale by themselves).
        float factor;
        using (var graphics = CreateGraphics())
        {
            factor = graphics.DpiX / 96F;
        }
        if (Math.Abs(factor - 1F) > 0.01F)
        {
            Scale(new SizeF(factor, factor));
        }
        _requirements.Columns.Add("Requirement", (int)(360 * factor));
        _requirements.Columns.Add("Status", (int)(230 * factor));

        CheckRequirements();
    }

    private static CheckBox AddCheckBox(Control parent, string text, int x, int y, int width)
    {
        var box = new CheckBox { Text = text, Checked = true, Bounds = new Rectangle(x, y, width, 22) };
        parent.Controls.Add(box);
        return box;
    }

    private void CheckRequirements()
    {
        var os = Environment.OSVersion.Version;
        var osOk = os.Major >= 10 && Environment.Is64BitOperatingSystem;
        AddRequirement("Windows 10/11, 64-bit", osOk, osOk ? $"OK (build {os.Build})" : "Not supported");

        bool hasApp;
        using (var payload = OpenPayload())
        {
            hasApp = payload is not null;
        }
        AddRequirement("NorthCommander (self-contained, no .NET needed)", hasApp,
            hasApp ? "Included" : "Missing: this setup build has no app inside");

        var hasPowerShell = File.Exists(_powerShell);
        AddRequirement("Windows PowerShell (for shortcuts and PATH)", hasPowerShell,
            hasPowerShell ? "Found" : "Not found: shortcuts and PATH are skipped");

        _ready = osOk && hasApp;
        _install.Enabled = _ready;
        Log(_ready
            ? "Ready. Choose the app folder and options, then click Install."
            : "NorthCommander can't be installed on this PC; see the requirements above.");
    }

    private void AddRequirement(string name, bool ok, string status)
    {
        var item = new ListViewItem(name) { ForeColor = ok ? Color.DarkGreen : Color.Firebrick };
        item.SubItems.Add(status);
        _requirements.Items.Add(item);
    }

    private static Stream? OpenPayload() =>
        typeof(SetupForm).Assembly.GetManifestResourceStream(PayloadResource);

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the NorthCommander app folder",
            ShowNewFolderButton = true,
        };
        if (Directory.Exists(_folder.Text))
        {
            dialog.SelectedPath = _folder.Text;
        }
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _folder.Text = dialog.SelectedPath;
        }
    }

    private async void OnInstall(object? sender, EventArgs e)
    {
        if (!TryGetTarget(out var target))
        {
            return;
        }
        var exe = Path.Combine(target, AppExe);
        if (IsRunning(exe))
        {
            Warn("NorthCommander is running from that folder. Close it first.");
            return;
        }

        var installArgs = new StringBuilder();
        if (!_startMenu.Checked) installArgs.Append(" -NoStartMenu");
        if (!_desktop.Checked) installArgs.Append(" -NoDesktop");
        if (!_path.Checked) installArgs.Append(" -NoPath");
        var integrate = _startMenu.Checked || _desktop.Checked || _path.Checked;
        var launch = _launch.Checked;

        SetBusy(true);
        try
        {
            Log($"== Unpacking NorthCommander {_version} to {target}");
            var progress = new Progress<int>(percent => _progress.Value = percent);
            await Task.Run(() => ExtractPayload(target, progress));

            if (integrate && File.Exists(_powerShell))
            {
                Log("== Creating shortcuts and PATH entry (install.ps1)");
                var script = Path.Combine(target, "install.ps1");
                var exitCode = await RunAsync(_powerShell, $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"{installArgs}", target);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"install.ps1 failed with exit code {exitCode}.");
                }
            }
            else if (integrate)
            {
                Log("Windows PowerShell not found; skipped shortcuts and PATH.");
            }

            _progress.Value = 100;
            Log($"Done. NorthCommander is set up in {target}");
            if (launch)
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = target, UseShellExecute = true })?.Dispose();
            }
            MessageBox.Show(this,
                $"NorthCommander is set up in{Environment.NewLine}{target}{Environment.NewLine}{Environment.NewLine}" +
                "To remove the shortcuts and PATH entry later, run uninstall.ps1 in that folder; delete the folder to remove NorthCommander completely.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or InvalidDataException or Win32Exception)
        {
            _progress.Value = 0;
            Log($"ERROR: {ex.Message}");
            MessageBox.Show(this, $"Setup failed:{Environment.NewLine}{ex.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryGetTarget(out string target)
    {
        target = string.Empty;
        var text = Environment.ExpandEnvironmentVariables(_folder.Text.Trim().Trim('"'));
        if (text.Length == 0)
        {
            Warn("Choose an app folder first.");
            return false;
        }
        try
        {
            target = Path.GetFullPath(text).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Warn($"Invalid folder: {text}");
            return false;
        }
        var root = Path.GetPathRoot(target)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;
        if (target.Length == 0 || string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            Warn("Choose a folder, not a drive root.");
            return false;
        }
        return true;
    }

    private static bool IsRunning(string exe)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppExe)))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // No access to that process (e.g. another user's): it can't be running from our folder.
                }
            }
        }
        return false;
    }

    /// <summary>Unpacks the embedded zip, replacing files one by one so a failure never leaves half-written files.</summary>
    private void ExtractPayload(string target, IProgress<int> progress)
    {
        using var stream = OpenPayload() ?? throw new InvalidDataException("This setup build contains no NorthCommander app.");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = target + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(target);

        long total = Math.Max(1, zip.Entries.Sum(entry => entry.Length));
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe path in the setup package: {entry.FullName}");
            }
            if (relative.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var temp = destination + ".setup-tmp";
            using (var input = entry.Open())
            using (var output = File.Create(temp))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    progress.Report((int)(done * 95 / total));
                }
            }
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            File.Move(temp, destination);
            Log($"  {relative}");
        }
    }

    private Task<int> RunAsync(string file, string arguments, string workingDirectory)
    {
        var encoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = encoding,
                StandardErrorEncoding = encoding,
            },
            EnableRaisingEvents = true,
        };
        var completion = new TaskCompletionSource<int>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
        process.Exited += (_, _) =>
        {
            process.WaitForExit(); // flushes the remaining output events
            completion.TrySetResult(process.ExitCode);
            process.Dispose();
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return completion.Task;
    }

    private void Log(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<string>(Log), text);
            return;
        }
        _log.AppendText(text.TrimEnd() + Environment.NewLine);
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var control in _lockedControls)
        {
            control.Enabled = !busy;
        }
        _install.Enabled = !busy && _ready;
        UseWaitCursor = busy;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_busy && MessageBox.Show(this, "Setup is still running. Close anyway?", Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            e.Cancel = true;
        }
        base.OnFormClosing(e);
    }
}
