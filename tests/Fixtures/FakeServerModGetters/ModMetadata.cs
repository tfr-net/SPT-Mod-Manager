using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace FakeServerModGetters;

// An alternative style: expression-bodied getters and a numeric Version constructor.
public sealed class ModMetadata : IModMetadata
{
    public string ModGuid { get => "com.test.getters"; init { } }

    public string Name { get => "Getter Style Mod"; init { } }

    public string Author { get => "Tester"; init { } }

    public List<string>? Contributors { get; init; }

    public Version Version { get => new(3, 4, 5); init { } }

    public Range SptVersion { get => new("^4.1.0"); init { } }

    public bool HasPrepatcher { get; init; }

    public List<string>? Incompatibilities { get; init; }

    public Dictionary<string, Range>? ModDependencies { get; init; }

    public string? Url { get; init; }

    public string License { get => "MIT"; init { } }
}
