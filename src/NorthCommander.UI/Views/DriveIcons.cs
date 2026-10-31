using Avalonia.Data.Converters;
using Avalonia.Media;
using NorthCommander.Core.Services;

namespace NorthCommander.UI.Views;

/// <summary>Small 16x16 vector icons for the drive dropdown.</summary>
public static class DriveIcons
{
    private static readonly Geometry Fixed = Geometry.Parse("F0 M1,5 H15 V11 H1 Z M11,7.5 H13 V9 H11 Z");
    private static readonly Geometry System = Geometry.Parse("M1,1 H7.5 V7.5 H1 Z M8.5,1 H15 V7.5 H8.5 Z M1,8.5 H7.5 V15 H1 Z M8.5,8.5 H15 V15 H8.5 Z");
    private static readonly Geometry Removable = Geometry.Parse("F0 M5,1 H11 V5 H5 Z M6,2 H7 V3.5 H6 Z M9,2 H10 V3.5 H9 Z M4,5 H12 V15 H4 Z");
    private static readonly Geometry Network = Geometry.Parse("F0 M2,1 H14 V10 H2 Z M3,2 H13 V9 H3 Z M7,10 H9 V12 H7 Z M2,12 H14 V14 H2 Z");
    private static readonly Geometry CdRom = Geometry.Parse("F0 M1,8 A7,7 0 1 1 15,8 A7,7 0 1 1 1,8 Z M6,8 A2,2 0 1 0 10,8 A2,2 0 1 0 6,8 Z");
    private static readonly Geometry Documents = Geometry.Parse("F0 M3,1 H10 L13,4 V15 H3 Z M5,7 H11 V8 H5 Z M5,10 H11 V11 H5 Z");

    public static readonly FuncValueConverter<DriveKind, Geometry> Icon = new(kind => kind switch
    {
        DriveKind.System => System,
        DriveKind.Removable => Removable,
        DriveKind.Network => Network,
        DriveKind.CdRom => CdRom,
        DriveKind.Documents => Documents,
        _ => Fixed,
    });
}
