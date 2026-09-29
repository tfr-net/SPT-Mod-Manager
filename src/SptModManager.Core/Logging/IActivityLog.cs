namespace SptModManager.Core.Logging;

public enum ActivityLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Sink for user-facing progress and status messages. The UI shows these in its activity panel.
/// </summary>
public interface IActivityLog
{
    void Write(ActivityLevel level, string message);
}

public static class ActivityLogExtensions
{
    public static void Info(this IActivityLog log, string message) => log.Write(ActivityLevel.Info, message);

    public static void Success(this IActivityLog log, string message) => log.Write(ActivityLevel.Success, message);

    public static void Warn(this IActivityLog log, string message) => log.Write(ActivityLevel.Warning, message);

    public static void Error(this IActivityLog log, string message) => log.Write(ActivityLevel.Error, message);
}

public sealed class NullActivityLog : IActivityLog
{
    public static readonly NullActivityLog Instance = new();

    public void Write(ActivityLevel level, string message)
    {
    }
}
