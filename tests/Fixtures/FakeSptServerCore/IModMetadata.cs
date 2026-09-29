using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace SPTarkov.Server.Core.Models.Spt.Mod;

// Mirrors the real SPT 4.x server mod metadata contract.
public interface IModMetadata
{
    string ModGuid { get; init; }

    string Name { get; init; }

    string Author { get; init; }

    List<string>? Contributors { get; init; }

    Version Version { get; init; }

    Range SptVersion { get; init; }

    bool HasPrepatcher { get; init; }

    List<string>? Incompatibilities { get; init; }

    Dictionary<string, Range>? ModDependencies { get; init; }

    string? Url { get; init; }

    string License { get; init; }
}
