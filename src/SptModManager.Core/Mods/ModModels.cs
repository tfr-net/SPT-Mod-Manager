namespace SptModManager.Core.Mods;

public sealed record OperationProgress(string Message, double? Fraction = null);

/// <summary>A request to install a specific Forge mod version.</summary>
public sealed record ModInstallRequest(
    int ForgeModId,
    string Name,
    string Version,
    string? Guid = null,
    string? Slug = null,
    string? DetailUrl = null,
    int? VersionId = null,
    string? Link = null,
    long? ContentLength = null);

public enum PlanActionKind
{
    Install,
    Update,
    Reinstall,
    Keep,
}

public sealed record PlanStep(
    PlanActionKind Kind,
    int ForgeModId,
    string? Guid,
    string Name,
    string? Slug,
    string? DetailUrl,
    string? FromVersion,
    string ToVersion,
    int? VersionId,
    string? Link,
    long? ContentLength,
    bool IsDependency,
    IReadOnlyList<int> DependsOn)
{
    public string Describe() => Kind switch
    {
        PlanActionKind.Install => $"Install {Name} {ToVersion}{(IsDependency ? " (dependency)" : string.Empty)}",
        PlanActionKind.Update => $"Update {Name} {FromVersion} -> {ToVersion}{(IsDependency ? " (dependency)" : string.Empty)}",
        PlanActionKind.Reinstall => $"Reinstall {Name} {ToVersion}",
        _ => $"Keep {Name} {FromVersion}",
    };
}

public sealed class InstallPlan
{
    public List<PlanStep> Steps { get; } = [];

    /// <summary>Blocking issues, such as a required dependency with no compatible version.</summary>
    public List<string> Problems { get; } = [];

    public List<string> Warnings { get; } = [];

    public IEnumerable<PlanStep> ActionableSteps => Steps.Where(s => s.Kind != PlanActionKind.Keep);

    public bool HasWork => ActionableSteps.Any();

    public long? TotalDownloadBytes =>
        ActionableSteps.All(s => s.ContentLength is not null) ? ActionableSteps.Sum(s => s.ContentLength!.Value) : null;
}

public enum ModUpdateStatus
{
    NotChecked,
    UpToDate,
    UpdateAvailable,
    Blocked,
    IncompatibleWithSpt,
    UnknownVersion,
    NotOnForge,
}

public sealed record ModUpdateInfo(
    ModUpdateStatus Status,
    string? LatestVersion = null,
    int? LatestVersionId = null,
    string? Link = null,
    long? ContentLength = null,
    string? Reason = null,
    string? RequiredSptVersion = null);

/// <summary>Some steps of an install plan failed; the others were installed.</summary>
public sealed class PlanExecutionException(string message, IReadOnlyList<string> failures) : Exception(message)
{
    public IReadOnlyList<string> Failures { get; } = failures;
}

public sealed record UninstallResult(IReadOnlyList<InstalledMod> BrokenDependents, IReadOnlyList<InstalledMod> OrphanedDependencies);
