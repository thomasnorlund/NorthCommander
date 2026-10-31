using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NorthCommander.Core.Services;

/// <summary>Runs command-line input (NC-style command line) in a folder through the system shell.</summary>
public static class CommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> via <c>cmd.exe /c</c> (Windows) or <c>/bin/sh -c</c> in <paramref name="workingDirectory"/>.
    /// Standard output and error are streamed to <paramref name="outputFile"/>; returns the exit code.
    /// </summary>
    public static async Task<int> RunAsync(string command, string workingDirectory, string outputFile, CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = CreateStartInfo(command, workingDirectory) };
        await using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(false));
        var gate = new Lock();
        void Write(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
            {
                lock (gate)
                {
                    writer.WriteLine(e.Data);
                }
            }
        }

        process.OutputDataReceived += Write;
        process.ErrorDataReceived += Write;
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        // The parameterless WaitForExit flushes the asynchronous output readers.
        process.WaitForExit();
        lock (gate)
        {
            writer.Flush();
        }

        return process.ExitCode;
    }

    public static ProcessStartInfo CreateStartInfo(string command, string workingDirectory)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (OperatingSystem.IsWindows())
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = "/c " + command;

            // Console tools write in the OEM code page.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            info.StandardOutputEncoding = oem;
            info.StandardErrorEncoding = oem;
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        return info;
    }

    /// <summary>Recognizes <c>cd</c> (which must change the panel's folder rather than a child process's) and resolves its target.</summary>
    public static bool TryParseChangeDirectory(string command, string currentDirectory, out string target)
    {
        target = string.Empty;
        command = command.Trim();
        if (!command.StartsWith("cd", StringComparison.OrdinalIgnoreCase)
            || (command.Length > 2 && command[2] is not (' ' or '\\' or '/' or '.')))
        {
            return false;
        }

        var argument = command[2..].Trim();
        if (argument.StartsWith("/d ", StringComparison.OrdinalIgnoreCase))
        {
            argument = argument[3..].Trim();
        }

        argument = argument.Trim('"');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (argument.Length == 0 || argument == "~")
        {
            target = home;
        }
        else
        {
            if (argument.StartsWith("~/", StringComparison.Ordinal))
            {
                argument = Path.Combine(home, argument[2..]);
            }

            target = Path.GetFullPath(Path.Combine(currentDirectory, argument));
        }

        return true;
    }
}
