using NorthCommander.Core.Services;

namespace NorthCommander.Core.Tests;

public class DriveServiceTests
{
    private static DriveEntry Drive(string root, DriveKind kind = DriveKind.Fixed) => new(root, root, "label", kind, true, 1, 2);

    [Fact]
    public void FindDrive_picks_the_longest_matching_mount_point()
    {
        var drives = new[] { Drive("/"), Drive("/media/usb") };

        Assert.Equal("/media/usb", DriveService.FindDrive(drives, "/media/usb/docs", StringComparison.Ordinal)?.RootPath);
        Assert.Equal("/media/usb", DriveService.FindDrive(drives, "/media/usb", StringComparison.Ordinal)?.RootPath);
        Assert.Equal("/", DriveService.FindDrive(drives, "/media/usbx/docs", StringComparison.Ordinal)?.RootPath);
    }

    [Fact]
    public void FindDrive_matches_windows_drive_letters_case_insensitively()
    {
        var drives = new[] { Drive(@"C:\", DriveKind.System), Drive(@"D:\") };

        Assert.Equal(@"D:\", DriveService.FindDrive(drives, @"d:\Projects\x.zip\docs", StringComparison.OrdinalIgnoreCase)?.RootPath);
        Assert.Equal(@"C:\", DriveService.FindDrive(drives, @"C:", StringComparison.OrdinalIgnoreCase)?.RootPath);
        Assert.Null(DriveService.FindDrive(drives, @"E:\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindDrive_ignores_the_documents_entry()
    {
        var drives = new[] { Drive("/"), Drive("/home/me/Documents", DriveKind.Documents) };

        Assert.Equal("/", DriveService.FindDrive(drives, "/home/me/Documents/a", StringComparison.Ordinal)?.RootPath);
    }

    [Fact]
    public void GetDrives_contains_the_current_directory_drive_and_documents()
    {
        var drives = DriveService.GetDrives();

        Assert.NotNull(DriveService.FindDrive(drives, Environment.CurrentDirectory));
        Assert.Contains(drives, d => d.Kind == DriveKind.Documents);
    }
}
