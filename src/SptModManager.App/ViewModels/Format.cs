using System.Globalization;

namespace SptModManager.App.ViewModels;

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Count(long count) => count switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    public static string Date(DateTimeOffset? date) => date?.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture) ?? string.Empty;
}
