using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NorthCommander.UI.Services;

/// <summary>Operating system shell actions offered in the file context menu.</summary>
internal static class ShellIntegration
{
    public static bool CanShowProperties => OperatingSystem.IsWindows();

    /// <summary>Opens the system file manager with the item selected (Explorer) or its folder opened (other OSes).</summary>
    public static void RevealInFileManager(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        }
        else
        {
            var folder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? path;
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
    }

    /// <summary>Shows the Windows shell "Properties" dialog of a file or folder.</summary>
    public static void ShowProperties(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var info = new ShellExecuteInfo
        {
            Size = Marshal.SizeOf<ShellExecuteInfo>(),
            Mask = SeeMaskInvokeIdList,
            Verb = "properties",
            File = path,
            Show = 1,
        };
        ShellExecuteEx(ref info);
    }

    private const uint SeeMaskInvokeIdList = 0x0000000C;

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public IntPtr InstanceApp;
        public IntPtr IdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }
}
