using Avalonia.Media;

namespace SptModManager.App.ViewModels;

/// <summary>Simple 24x24 icon geometries for the navigation.</summary>
public static class Icons
{
    public static Geometry Browse { get; } = StreamGeometry.Parse(
        "F0 M10,2 A8,8 0 1 1 10,18 A8,8 0 1 1 10,2 Z M10,4.5 A5.5,5.5 0 1 0 10,15.5 A5.5,5.5 0 1 0 10,4.5 Z M15.2,16.6 L16.6,15.2 L22,20.6 L20.6,22 Z");

    public static Geometry Installed { get; } = StreamGeometry.Parse(
        "M3,4 H6 V7 H3 Z M8,4.5 H21 V6.5 H8 Z M3,10.5 H6 V13.5 H3 Z M8,11 H21 V13 H8 Z M3,17 H6 V20 H3 Z M8,17.5 H21 V19.5 H8 Z");

    public static Geometry Spt { get; } = StreamGeometry.Parse(
        "M11,2 H13 V13.2 L16.6,9.6 L18,11 L12,17 L6,11 L7.4,9.6 L11,13.2 Z M4,19 H20 V21.5 H4 Z");

    public static Geometry Settings { get; } = StreamGeometry.Parse(
        "M3,5 H21 V7 H3 Z M3,11 H21 V13 H3 Z M3,17 H21 V19 H3 Z M6,3 H10 V9 H6 Z M14,9 H18 V15 H14 Z M8,15 H12 V21 H8 Z");
}
