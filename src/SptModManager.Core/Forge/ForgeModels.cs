using System.Text.Json.Serialization;

namespace SptModManager.Core.Forge;

// Models for The Forge API v0 (https://sp-mod.com/api/v0). JSON uses snake_case and is mapped by
// ForgeClient's serializer options, so property names here are plain PascalCase.

public sealed class ForgeEnvelope<T>
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public ForgePageMeta? Meta { get; init; }
}

public sealed class ForgePageMeta
{
    public int CurrentPage { get; init; }

    public int LastPage { get; init; }

    public int PerPage { get; init; }

    public int Total { get; init; }
}

public sealed record ForgePage<T>(IReadOnlyList<T> Items, int CurrentPage, int LastPage, int Total)
{
    public bool HasNext => CurrentPage < LastPage;

    public bool HasPrevious => CurrentPage > 1;
}

public sealed class ForgeUser
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? ProfilePhotoUrl { get; init; }
}

public sealed class ForgeCategory
{
    public int Id { get; init; }

    /// <summary>Populated when the category is included on a mod.</summary>
    public string? Name { get; init; }

    /// <summary>Populated by the /mod-categories endpoint.</summary>
    public string? Title { get; init; }

    public string Slug { get; init; } = string.Empty;

    public string? ColorClass { get; init; }

    [JsonIgnore]
    public string DisplayName => Name ?? Title ?? Slug;
}

public sealed class ForgeLicense
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? ShortName { get; init; }
}

public sealed class ForgeLink
{
    public string Url { get; init; } = string.Empty;

    public string? Label { get; init; }
}

public sealed class ForgeMod
{
    public int Id { get; init; }

    public string? Guid { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    public string? Teaser { get; init; }

    public string? Description { get; init; }

    public string? Thumbnail { get; init; }

    public long Downloads { get; init; }

    public int? FavouritesCount { get; init; }

    public ForgeUser? Owner { get; init; }

    public List<ForgeUser>? AdditionalAuthors { get; init; }

    public List<ForgeLink>? SourceCodeLinks { get; init; }

    public string? DetailUrl { get; init; }

    /// <summary>True when any published version is confirmed Fika compatible.</summary>
    public bool? FikaCompatibility { get; init; }

    public bool Featured { get; init; }

    public bool ContainsAds { get; init; }

    public bool ContainsAiContent { get; init; }

    public ForgeCategory? Category { get; init; }

    public ForgeLicense? License { get; init; }

    /// <summary>The latest (up to six) versions, when requested with include=versions.</summary>
    public List<ForgeVersionSummary>? Versions { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class ForgeVersionSummary
{
    public int Id { get; init; }

    public string Version { get; init; } = string.Empty;

    public string? SptVersionConstraint { get; init; }

    public long Downloads { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed class ForgeModVersion
{
    public int Id { get; init; }

    public string Version { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>Forge download URL; it redirects to the author's direct download.</summary>
    public string? Link { get; init; }

    public long? ContentLength { get; init; }

    public string? SptVersionConstraint { get; init; }

    public long Downloads { get; init; }

    /// <summary>One of "compatible", "incompatible" or "unknown".</summary>
    public string? FikaCompatibility { get; init; }

    public List<ForgeLink>? VirusTotalLinks { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed class ForgeResolvedVersion
{
    public int Id { get; init; }

    public string Version { get; init; } = string.Empty;

    public string? Link { get; init; }

    public long? ContentLength { get; init; }

    public string? FikaCompatibility { get; init; }

    public List<string>? SptVersions { get; init; }
}

public sealed class ForgeDependencyNode
{
    public int Id { get; init; }

    public string? Guid { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Slug { get; init; } = string.Empty;

    /// <summary>Null when nothing satisfies the constraint on the requested SPT version.</summary>
    public ForgeResolvedVersion? LatestCompatibleVersion { get; init; }

    /// <summary>True when no single version satisfies every queried mod's constraint.</summary>
    public bool Conflict { get; init; }

    public List<ForgeDependencyNode> Dependencies { get; init; } = [];
}

public sealed class ForgeFileTree
{
    public DateTimeOffset? VerifiedAt { get; init; }

    public int FileCount { get; init; }

    public bool Truncated { get; init; }

    public List<string> Files { get; init; } = [];
}

public sealed class ForgeSptVersion
{
    public int Id { get; init; }

    public string Version { get; init; } = string.Empty;

    public int VersionMajor { get; init; }

    public int VersionMinor { get; init; }

    public int VersionPatch { get; init; }

    public string? VersionLabels { get; init; }

    public int ModCount { get; init; }
}

// /mods/updates response

public sealed class ForgeUpdateCheck
{
    public string SptVersion { get; init; } = string.Empty;

    public List<ForgeUpdate> Updates { get; init; } = [];

    public List<ForgeBlockedUpdate> BlockedUpdates { get; init; } = [];

    public List<ForgeUpdateModRef> UpToDate { get; init; } = [];

    public List<ForgeUpdateModRef> IncompatibleWithSpt { get; init; } = [];
}

public sealed class ForgeUpdateModRef
{
    /// <summary>The installed mod version ID.</summary>
    public int Id { get; init; }

    public int ModId { get; init; }

    public string? Guid { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Slug { get; init; }

    public string Version { get; init; } = string.Empty;

    public string? Reason { get; init; }

    public ForgeResolvedVersion? LatestCompatibleVersion { get; init; }
}

public sealed class ForgeUpdate
{
    public ForgeUpdateModRef CurrentVersion { get; init; } = new();

    public ForgeResolvedVersion RecommendedVersion { get; init; } = new();

    public string? UpdateReason { get; init; }
}

public sealed class ForgeBlockedUpdate
{
    public ForgeUpdateModRef CurrentVersion { get; init; } = new();

    public ForgeResolvedVersion? LatestVersion { get; init; }

    public string? BlockReason { get; init; }

    public List<ForgeBlockingMod> BlockingMods { get; init; } = [];
}

public sealed class ForgeBlockingMod
{
    public int ModId { get; init; }

    public string? ModGuid { get; init; }

    public string? ModName { get; init; }

    public string? CurrentVersion { get; init; }

    public string? Constraint { get; init; }

    public string? IncompatibleWith { get; init; }
}
