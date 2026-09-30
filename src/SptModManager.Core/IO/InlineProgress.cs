namespace SptModManager.Core.IO;

/// <summary>
/// An <see cref="IProgress{T}"/> that runs its callback immediately on the reporting thread. Used to rescale or
/// relabel progress on its way to the caller's own progress handler; unlike <see cref="Progress{T}"/> it cannot
/// deliver reports out of order (an old "93%" landing after "100%").
/// </summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
