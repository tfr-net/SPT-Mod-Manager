using SPTarkov.Server.Core.Models.Spt.Mod;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace FakeServerMod;

// The style used by SPT's own example mods: init-only properties with initializers.
public sealed record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.test.server";

    public string Name { get; init; } = "Test Server Mod";

    public string Author { get; init; } = "Tester";

    public List<string>? Contributors { get; init; } = ["Someone", "Someone Else"];

    public Version Version { get; init; } = new("2.0.1");

    public Range SptVersion { get; init; } = new("~4.1.0");

    public bool HasPrepatcher { get; init; }

    public List<string>? Incompatibilities { get; init; }

    public Dictionary<string, Range>? ModDependencies { get; init; } = new() { { "com.test.client", new Range("^1.0.0") } };

    public string? Url { get; init; } = "https://example.com";

    public string License { get; init; } = "MIT";
}
