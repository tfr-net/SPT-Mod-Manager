using Avalonia;
using Avalonia.Media;

namespace SptModManager.App.ViewModels;

/// <summary>
/// Colorful stand-in tiles for mods without a picture: each mod gets its own gradient (stable across runs, derived
/// from its name) with its initials on top.
/// </summary>
public static class Avatar
{
    public static string Initials(string? name)
    {
        var words = (name ?? string.Empty)
            .Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]))
            .ToList();

        return words.Count switch
        {
            0 => "?",
            1 => words[0][..1].ToUpperInvariant(),
            _ => string.Concat(words[0][0], words[1][0]).ToUpperInvariant(),
        };
    }

    public static IBrush Brush(string? key)
    {
        var hue = StableHash(key ?? string.Empty) % 360;
        var light = new HsvColor(1, hue, 0.55, 0.78).ToRgb();
        var dark = new HsvColor(1, (hue + 40) % 360, 0.72, 0.42).ToRgb();

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(light, 0), new GradientStop(dark, 1) },
        }.ToImmutable();
    }

    /// <summary>FNV-1a; string.GetHashCode is randomized per process, which would reshuffle colors every launch.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash;
    }
}
