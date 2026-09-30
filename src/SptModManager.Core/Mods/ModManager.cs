using SptModManager.Core.Forge;
using SptModManager.Core.IO;
using SptModManager.Core.Logging;
using SptModManager.Core.Net;
using SptModManager.Core.Spt;
using SptModManager.Core.Versioning;

namespace SptModManager.Core.Mods;

/// <summary>
/// Installs, updates, removes and tracks mods for one SPT install, using The Forge for metadata, dependency
/// resolution and update checks.
/// </summary>
public sealed class ModManager
{
    /// <summary>Upper bound on per-mod fallback lookups during an update check, to stay polite to the API.</summary>
    private const int MaxVersionFallbackLookups = 40;

    private readonly SptInstallation _install;
    private readonly IForgeClient _forge;
    private readonly IFileDownloader _downloader;
    private readonly IActivityLog _log;
    private readonly string _downloadDirectory;
    private readonly InstalledModsStore _store;
    private InstalledModsState _state;
    private (string SptVersion, IReadOnlyList<string> Patches)? _earlierPatches;

    public ModManager(SptInstallation install, IForgeClient forge, IFileDownloader downloader, IActivityLog log, string downloadDirectory)
    {
        _install = install;
        _forge = forge;
        _downloader = downloader;
        _log = log;
        _downloadDirectory = downloadDirectory;
        _store = new InstalledModsStore(install);
        _state = _store.Load();
    }

    public SptInstallation Installation => _install;

    public IReadOnlyList<InstalledMod> Mods => _state.Mods;

    public InstalledMod? FindByForgeId(int forgeModId) => _state.Mods.FirstOrDefault(m => m.ForgeModId == forgeModId);

    public InstalledMod? FindByGuid(string? guid) => guid is null ? null : _state.Mods.FirstOrDefault(m => m.MatchesGuid(guid));

    // ---------------------------------------------------------------------------------------------------------------
    // Scan & match
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reconciles tracked mods with what is on disk and, when asked, matches unknown mods to The Forge by GUID.
    /// </summary>
    public async Task RefreshAsync(bool matchWithForge, CancellationToken cancellationToken = default)
    {
        _state = _store.Load();
        var components = ModScanner.Scan(_install);

        PruneMissingFiles();
        ClaimComponents(components);

        if (matchWithForge)
        {
            await MatchDetectedModsAsync(cancellationToken);
        }

        _state.Mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _store.Save(_state);
    }

    private void PruneMissingFiles()
    {
        foreach (var mod in _state.Mods.ToList())
        {
            mod.Files.RemoveAll(f => !File.Exists(Path.Combine(_install.RootPath, f)));

            if (mod.Files.Count == 0)
            {
                _state.Mods.Remove(mod);
                if (mod.Source == InstallSource.Manager)
                {
                    _log.Warn($"{mod.Name} is no longer on disk and was removed from the list.");
                }
            }
        }
    }

    private void ClaimComponents(IReadOnlyList<LocalModComponent> components)
    {
        var owners = BuildFileOwnerMap();

        foreach (var component in components)
        {
            var owner = owners.GetValueOrDefault(component.PrimaryPath)
                        ?? component.Files.Select(f => owners.GetValueOrDefault(f)).FirstOrDefault(o => o is not null)
                        ?? FindByGuid(component.Guid);

            if (owner is null)
            {
                owner = new InstalledMod
                {
                    Guid = component.Guid,
                    Name = component.Name,
                    Version = component.Version,
                    Source = InstallSource.Detected,
                };
                _state.Mods.Add(owner);
            }
            else if (owner.Source == InstallSource.Detected
                     && component.Version is not null
                     && (owner.Guid is null || string.Equals(owner.Guid, component.Guid, StringComparison.OrdinalIgnoreCase))
                     && !VersionUtil.AreEquivalent(owner.Version, component.Version))
            {
                // A hand-installed mod was updated by hand; follow the version on disk.
                owner.Version = component.Version;
                owner.ForgeVersionId = null;
            }

            if (component.Guid is not null && !owner.MatchesGuid(component.Guid))
            {
                if (owner.Guid is null)
                {
                    owner.Guid = component.Guid;
                }
                else
                {
                    owner.ComponentGuids.Add(component.Guid);
                }
            }

            // Manager installs track exactly what their archive wrote. Anything else in their folders (configs the
            // mod generated at runtime) stays unowned so updates and uninstalls never delete it.
            if (owner.Source == InstallSource.Manager)
            {
                continue;
            }

            foreach (var file in component.Files)
            {
                if (!owners.ContainsKey(file))
                {
                    owner.Files.Add(file);
                    owners[file] = owner;
                }
            }
        }
    }

    private async Task MatchDetectedModsAsync(CancellationToken cancellationToken)
    {
        var unmatched = _state.Mods.Where(m => m.ForgeModId is null && (m.Guid is not null || m.ComponentGuids.Count > 0)).ToList();
        if (unmatched.Count == 0)
        {
            return;
        }

        var guids = unmatched.SelectMany(m => m.ComponentGuids.Prepend(m.Guid!)).Where(g => g is not null);
        var forgeMods = await _forge.GetModsByGuidsAsync(guids, cancellationToken);
        var matched = 0;

        foreach (var forgeMod in forgeMods.Where(f => f.Guid is not null))
        {
            var candidates = unmatched.Where(m => m.MatchesGuid(forgeMod.Guid)).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            // If the same Forge mod already has an entry (e.g. installed via the manager), merge into it.
            var target = FindByForgeId(forgeMod.Id) ?? candidates[0];
            foreach (var other in candidates.Where(c => c != target))
            {
                MergeInto(target, other);
            }

            ApplyForgeIdentity(target, forgeMod);
            ResolveVersionId(target, forgeMod);
            matched++;

            if (target.ForgeVersionId is { } versionId)
            {
                await ClaimFromFileTreeAsync(target, forgeMod.Id, versionId, cancellationToken);
            }
        }

        if (matched > 0)
        {
            _log.Info($"Matched {matched} installed mod(s) to The Forge.");
        }
    }

    private void ApplyForgeIdentity(InstalledMod mod, ForgeMod forgeMod)
    {
        if (mod.Guid is not null && !string.Equals(mod.Guid, forgeMod.Guid, StringComparison.OrdinalIgnoreCase))
        {
            mod.ComponentGuids.Add(mod.Guid);
        }

        mod.ForgeModId = forgeMod.Id;
        mod.Guid = forgeMod.Guid;
        mod.Name = forgeMod.Name;
        mod.Slug = forgeMod.Slug;
        mod.DetailUrl = forgeMod.DetailUrl;
        mod.ComponentGuids = mod.ComponentGuids
            .Where(g => !string.Equals(g, mod.Guid, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ResolveVersionId(InstalledMod mod, ForgeMod forgeMod)
    {
        var match = forgeMod.Versions?.FirstOrDefault(v => VersionUtil.AreEquivalent(v.Version, mod.Version));
        if (match is not null)
        {
            mod.Version = match.Version;
            mod.ForgeVersionId = match.Id;
        }
    }

    /// <summary>
    /// Uses the Forge file listing of the installed version to pull other unmatched pieces (for example a server mod
    /// with its own GUID) into the matched mod, so the whole mod is tracked and removed together.
    /// </summary>
    private async Task ClaimFromFileTreeAsync(InstalledMod mod, int forgeModId, int versionId, CancellationToken cancellationToken)
    {
        ForgeFileTree? tree;
        try
        {
            tree = await _forge.GetFileTreeAsync(forgeModId, versionId, cancellationToken);
        }
        catch (ForgeApiException)
        {
            return;
        }

        if (tree is null || tree.Files.Count == 0)
        {
            return;
        }

        var layout = ArchiveLayout.Plan(tree.Files, _install.DataFolderName);
        var expected = layout.Map.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var other in _state.Mods.Where(m => m != mod && m.ForgeModId is null).ToList())
        {
            if (other.Files.Count > 0 && other.Files.All(f => expected.Contains(f)))
            {
                MergeInto(mod, other);
            }
        }
    }

    private void MergeInto(InstalledMod target, InstalledMod other)
    {
        foreach (var guid in other.ComponentGuids.Prepend(other.Guid).OfType<string>())
        {
            if (!target.MatchesGuid(guid))
            {
                target.ComponentGuids.Add(guid);
            }
        }

        target.Files = target.Files.Union(other.Files, StringComparer.OrdinalIgnoreCase).ToList();
        _state.Mods.Remove(other);
    }

    private Dictionary<string, InstalledMod> BuildFileOwnerMap()
    {
        var owners = new Dictionary<string, InstalledMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _state.Mods)
        {
            foreach (var file in mod.Files)
            {
                owners.TryAdd(file, mod);
            }
        }

        return owners;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Dependency resolution & install planning
    // ---------------------------------------------------------------------------------------------------------------

    public async Task<InstallPlan> PlanInstallAsync(IReadOnlyList<ModInstallRequest> requests, CancellationToken cancellationToken = default)
    {
        var sptVersion = RequireSptVersion();
        var plan = new InstallPlan();
        var steps = new Dictionary<int, PlanStep>();
        var order = new List<int>();

        var rootIds = requests.Select(r => r.ForgeModId).ToHashSet();
        var pairs = requests.Select(r => new ModVersionPair(r.ForgeModId.ToString(), r.Version)).ToList();
        var trees = await ResolveDependenciesAsync(pairs, cancellationToken);

        foreach (var request in requests)
        {
            var key = $"{request.ForgeModId}:{request.Version}";
            var directDependencies = new List<int>();

            if (trees.TryGetValue(key, out var nodes))
            {
                foreach (var node in nodes)
                {
                    directDependencies.Add(node.Id);
                    VisitDependency(node, request.Name, rootIds, plan, steps, order, sptVersion);
                }
            }
            else
            {
                plan.Warnings.Add($"The Forge could not resolve dependencies for {request.Name} {request.Version}; it will be installed on its own.");
            }

            var resolved = await CompleteRequestAsync(request, cancellationToken);
            var existing = FindByForgeId(request.ForgeModId) ?? FindByGuid(request.Guid);
            var kind = existing is null
                ? PlanActionKind.Install
                : VersionUtil.AreEquivalent(existing.Version, resolved.Version) ? PlanActionKind.Reinstall : PlanActionKind.Update;

            steps[request.ForgeModId] = new PlanStep(kind, resolved.ForgeModId, resolved.Guid, resolved.Name, resolved.Slug, resolved.DetailUrl,
                existing?.Version, resolved.Version, resolved.VersionId, resolved.Link, resolved.ContentLength, IsDependency: false, directDependencies);

            order.Remove(request.ForgeModId);
            order.Add(request.ForgeModId);
        }

        plan.Steps.AddRange(order.Select(id => steps[id]));

        foreach (var step in plan.ActionableSteps.Where(s => string.IsNullOrWhiteSpace(s.Link)))
        {
            plan.Problems.Add($"No download link is available for {step.Name} {step.ToVersion}.");
        }

        return plan;
    }

    /// <summary>
    /// Resolves dependency trees for the installed SPT version. The Forge only links a mod version to the SPT versions
    /// its author named, so dependencies left without a version are filled in from lookups against earlier patches of
    /// the same minor version, since mods made for those keep working on later patches.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, List<ForgeDependencyNode>>> ResolveDependenciesAsync(
        IReadOnlyList<ModVersionPair> pairs,
        CancellationToken cancellationToken = default)
    {
        var trees = await _forge.ResolveDependenciesAsync(pairs, RequireSptVersion(), cancellationToken);
        if (!trees.Values.Any(HasUnresolved))
        {
            return trees;
        }

        foreach (var earlier in await GetEarlierSptPatchesAsync(cancellationToken))
        {
            IReadOnlyDictionary<string, List<ForgeDependencyNode>> alternative;
            try
            {
                alternative = await _forge.ResolveDependenciesAsync(pairs, earlier, cancellationToken);
            }
            catch (ForgeApiException)
            {
                continue;
            }

            foreach (var (key, nodes) in trees)
            {
                if (alternative.TryGetValue(key, out var alternativeNodes))
                {
                    FillUnresolved(nodes, alternativeNodes);
                }
            }

            if (!trees.Values.Any(HasUnresolved))
            {
                break;
            }
        }

        return trees;
    }

    private static bool HasUnresolved(List<ForgeDependencyNode> nodes) =>
        nodes.Any(n => n.LatestCompatibleVersion is null || HasUnresolved(n.Dependencies));

    private static void FillUnresolved(List<ForgeDependencyNode> nodes, List<ForgeDependencyNode> alternativeTree)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].LatestCompatibleVersion is null && FindNode(alternativeTree, nodes[i].Id) is { LatestCompatibleVersion: not null } replacement)
            {
                nodes[i] = replacement;
                continue;
            }

            FillUnresolved(nodes[i].Dependencies, alternativeTree);
        }
    }

    private static ForgeDependencyNode? FindNode(List<ForgeDependencyNode> nodes, int modId)
    {
        foreach (var node in nodes)
        {
            if (node.Id == modId)
            {
                return node;
            }

            if (FindNode(node.Dependencies, modId) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Earlier published patches of the installed SPT minor version, newest first (cached).</summary>
    private async Task<IReadOnlyList<string>> GetEarlierSptPatchesAsync(CancellationToken cancellationToken)
    {
        var sptVersion = RequireSptVersion();
        if (_earlierPatches is { } cached && cached.SptVersion == sptVersion)
        {
            return cached.Patches;
        }

        try
        {
            var published = await _forge.GetSptVersionsAsync(cancellationToken);
            var patches = SptCompatibility.EarlierPatches(sptVersion, published.Select(v => v.Version));
            _earlierPatches = (sptVersion, patches);
            return patches;
        }
        catch (ForgeApiException)
        {
            return [];
        }
    }

    private void VisitDependency(
        ForgeDependencyNode node,
        string requiredBy,
        HashSet<int> rootIds,
        InstallPlan plan,
        Dictionary<int, PlanStep> steps,
        List<int> order,
        string sptVersion)
    {
        // Post-order: children first so dependencies install before the mods that need them.
        foreach (var child in node.Dependencies)
        {
            VisitDependency(child, node.Name, rootIds, plan, steps, order, sptVersion);
        }

        if (steps.ContainsKey(node.Id) || rootIds.Contains(node.Id))
        {
            return;
        }

        var version = node.LatestCompatibleVersion;
        if (version is null)
        {
            var installed = FindByForgeId(node.Id) ?? FindByGuid(node.Guid);
            if (installed is null)
            {
                plan.Problems.Add($"{node.Name} (required by {requiredBy}) has no version compatible with SPT {sptVersion}.");
            }
            else
            {
                plan.Warnings.Add($"{node.Name} (required by {requiredBy}) has no version compatible with SPT {sptVersion}; keeping your installed {installed.Version}.");
            }

            return;
        }

        if (node.Conflict)
        {
            plan.Warnings.Add($"Mods need different versions of {node.Name}; {version.Version} will be used.");
        }

        var existing = FindByForgeId(node.Id) ?? FindByGuid(node.Guid);
        var dependsOn = node.Dependencies.Select(d => d.Id).ToList();
        PlanActionKind kind;

        if (existing is null)
        {
            kind = PlanActionKind.Install;
        }
        else if (VersionUtil.AreEquivalent(existing.Version, version.Version))
        {
            kind = PlanActionKind.Keep;
        }
        else if (VersionUtil.Compare(existing.Version, version.Version) > 0)
        {
            kind = PlanActionKind.Keep;
            plan.Warnings.Add($"{requiredBy} asks for {node.Name} {version.Version}; you have the newer {existing.Version}, which will be kept.");
        }
        else
        {
            kind = PlanActionKind.Update;
        }

        steps[node.Id] = new PlanStep(kind, node.Id, node.Guid, node.Name, node.Slug, null, existing?.Version, version.Version, version.Id,
            version.Link, version.ContentLength, IsDependency: true, dependsOn);
        order.Add(node.Id);
    }

    /// <summary>Fills in the version ID and download link for a request when the caller did not supply them.</summary>
    private async Task<ModInstallRequest> CompleteRequestAsync(ModInstallRequest request, CancellationToken cancellationToken)
    {
        if (request.Link is not null && request.VersionId is not null)
        {
            return request;
        }

        var versions = await _forge.GetModVersionsAsync(request.ForgeModId, cancellationToken: cancellationToken);
        var match = versions.FirstOrDefault(v => v.Version == request.Version)
                    ?? versions.FirstOrDefault(v => VersionUtil.AreEquivalent(v.Version, request.Version));

        return match is null
            ? request
            : request with
            {
                Version = match.Version,
                VersionId = match.Id,
                Link = match.Link,
                ContentLength = match.ContentLength,
            };
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Install / update execution
    // ---------------------------------------------------------------------------------------------------------------

    public async Task ExecutePlanAsync(InstallPlan plan, IProgress<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var work = plan.ActionableSteps.ToList();

        for (var i = 0; i < work.Count; i++)
        {
            var step = work[i];
            var prefix = work.Count > 1 ? $"[{i + 1}/{work.Count}] " : string.Empty;

            progress?.Report(new OperationProgress($"{prefix}Downloading {step.Name} {step.ToVersion}...", 0));
            _log.Info($"{prefix}{step.Describe()}");

            var download = await _downloader.DownloadAsync(
                step.Link ?? throw new InvalidOperationException($"No download link for {step.Name}."),
                _downloadDirectory,
                new InlineProgress<DownloadProgress>(p => progress?.Report(new OperationProgress($"{prefix}Downloading {step.Name} {step.ToVersion}...", p.Fraction))),
                cancellationToken);

            try
            {
                progress?.Report(new OperationProgress($"{prefix}Installing {step.Name} {step.ToVersion}...", 0));
                InstallArchive(
                    step,
                    download.FilePath,
                    new InlineProgress<double>(f => progress?.Report(new OperationProgress($"{prefix}Installing {step.Name} {step.ToVersion}... {f:P0}", f))),
                    cancellationToken);
            }
            finally
            {
                TryDelete(download.FilePath);
            }

            _log.Success($"{step.Name} {step.ToVersion} installed.");
        }

        progress?.Report(new OperationProgress("Done.", 1));
    }

    /// <summary>
    /// Extracts a downloaded mod archive into the game folder and records the files it owns. Files from the previous
    /// version that the new one no longer ships are removed; config files the mod created at runtime are untouched.
    /// </summary>
    internal void InstallArchive(PlanStep step, string archivePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var layout = ArchiveLayout.Plan(ArchiveExtractor.ListFiles(archivePath), _install.DataFolderName);
        foreach (var warning in layout.Warnings)
        {
            _log.Warn($"{step.Name}: {warning}");
        }

        if (!layout.Success)
        {
            throw new InvalidOperationException($"{step.Name}: {layout.Error}");
        }

        var existing = FindByForgeId(step.ForgeModId) ?? FindByGuid(step.Guid);
        var owners = BuildFileOwnerMap();

        foreach (var target in layout.Map.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (owners.TryGetValue(target, out var owner) && owner != existing)
            {
                _log.Warn($"{step.Name} replaces {target}, which belonged to {owner.Name}.");
                owner.Files.RemoveAll(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
            }
        }

        // The extractor hands back normalized paths ("a/b.dll") whatever separators the archive used.
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, target) in layout.Map)
        {
            targets.TryAdd(PathUtil.NormalizeRelative(key), target);
        }

        var written = ArchiveExtractor.Extract(archivePath, _install.RootPath, key => targets.GetValueOrDefault(key), progress, cancellationToken);

        if (existing is not null)
        {
            // A manager install's file list is exactly what its archive wrote, so leftovers are safe to remove. A
            // hand-installed mod's list is everything in its folders (including configs), so only old DLLs go, which
            // would otherwise load twice after a rename.
            var stale = existing.Files.Except(written, StringComparer.OrdinalIgnoreCase)
                .Where(f => existing.Source == InstallSource.Manager || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
            DeleteFiles(stale);
        }

        var mod = existing ?? new InstalledMod();
        mod.ForgeModId = step.ForgeModId;
        mod.ForgeVersionId = step.VersionId;
        mod.Guid = step.Guid ?? mod.Guid;
        mod.Name = step.Name;
        mod.Version = step.ToVersion;
        mod.Slug = step.Slug ?? mod.Slug;
        mod.DetailUrl = step.DetailUrl ?? mod.DetailUrl;
        mod.Source = InstallSource.Manager;
        mod.InstalledAsDependency = existing is null ? step.IsDependency : existing.InstalledAsDependency && step.IsDependency;
        mod.DependsOn = step.DependsOn.ToList();
        mod.Files = written.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        mod.InstalledAt = DateTimeOffset.UtcNow;
        mod.ComponentGuids = ReadComponentGuids(mod.Files)
            .Where(g => !string.Equals(g, mod.Guid, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existing is null)
        {
            // Drop any detected entry for the same files so the mod is not listed twice.
            var writtenSet = mod.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _state.Mods.RemoveAll(m => m.Source == InstallSource.Detected && m.ForgeModId is null && m.Files.All(writtenSet.Contains));
            _state.Mods.Add(mod);
        }

        _state.Mods.RemoveAll(m => m != mod && m.Files.Count == 0);
        _store.Save(_state);
    }

    private IEnumerable<string> ReadComponentGuids(IEnumerable<string> files)
    {
        foreach (var file in files.Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            var fullPath = Path.Combine(_install.RootPath, file);
            foreach (var plugin in AssemblyInspector.ReadBepInPlugins(fullPath))
            {
                yield return plugin.Guid;
            }

            if (file.Contains("/user/mods/", StringComparison.OrdinalIgnoreCase) && AssemblyInspector.ReadServerModMetadata(fullPath)?.Guid is { } guid)
            {
                yield return guid;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Uninstall
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Installed mods that list <paramref name="mod"/> as a direct dependency.</summary>
    public IReadOnlyList<InstalledMod> GetDependents(InstalledMod mod) =>
        mod.ForgeModId is { } id ? _state.Mods.Where(m => m != mod && m.DependsOn.Contains(id)).ToList() : [];

    public UninstallResult Uninstall(InstalledMod mod)
    {
        var dependents = GetDependents(mod);

        DeleteFiles(mod.Files);
        _state.Mods.Remove(mod);
        _store.Save(_state);
        _log.Success($"{mod.Name} removed.");

        var orphans = mod.DependsOn
            .Select(FindByForgeId)
            .OfType<InstalledMod>()
            .Where(d => d.InstalledAsDependency && GetDependents(d).Count == 0)
            .ToList();

        return new UninstallResult(dependents, orphans);
    }

    private void DeleteFiles(IEnumerable<string> relativeFiles)
    {
        var protectedRoots = new[] { _install.PluginsPath, _install.PatchersPath, _install.ServerModsPath, _install.BepInExPath, _install.DataPath };

        foreach (var relative in relativeFiles)
        {
            string fullPath;
            try
            {
                fullPath = PathUtil.SafeCombine(_install.RootPath, relative);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (!File.Exists(fullPath))
            {
                continue;
            }

            File.SetAttributes(fullPath, FileAttributes.Normal);
            File.Delete(fullPath);

            var stopAt = protectedRoots
                .Where(r => fullPath.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Length)
                .FirstOrDefault() ?? _install.RootPath;

            PathUtil.PruneEmptyDirectories(Path.GetDirectoryName(fullPath)!, stopAt);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Updates
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Checks every tracked mod against The Forge. Keys are <see cref="InstalledMod.Key"/>.</summary>
    public async Task<IReadOnlyDictionary<string, ModUpdateInfo>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var sptVersion = RequireSptVersion();
        var results = new Dictionary<string, ModUpdateInfo>();

        foreach (var mod in _state.Mods.Where(m => m.ForgeModId is null))
        {
            results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.NotOnForge);
        }

        var forgeMods = _state.Mods.Where(m => m.ForgeModId is not null && !string.IsNullOrWhiteSpace(m.Version)).ToList();
        foreach (var mod in _state.Mods.Where(m => m.ForgeModId is not null && string.IsNullOrWhiteSpace(m.Version)))
        {
            results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.UnknownVersion, Reason: "The installed version could not be read.");
        }

        if (forgeMods.Count == 0)
        {
            return results;
        }

        var check = await _forge.CheckUpdatesAsync(forgeMods.Select(m => new ModVersionPair(m.ForgeModId!.Value.ToString(), m.Version!)), sptVersion, cancellationToken);
        ApplyUpdateCheck(check, forgeMods, results);

        // Mods Forge did not recognize usually have a version string that differs cosmetically ("1.2" vs "1.2.0").
        var unrecognized = forgeMods.Where(m => !results.ContainsKey(m.Key)).Take(MaxVersionFallbackLookups).ToList();
        var retry = new List<InstalledMod>();

        foreach (var mod in unrecognized)
        {
            var versions = await _forge.GetModVersionsAsync(mod.ForgeModId!.Value, cancellationToken: cancellationToken);
            var equivalent = versions.FirstOrDefault(v => VersionUtil.AreEquivalent(v.Version, mod.Version));

            if (equivalent is not null && equivalent.Version != mod.Version)
            {
                mod.Version = equivalent.Version;
                mod.ForgeVersionId = equivalent.Id;
                retry.Add(mod);
                continue;
            }

            var latest = (await _forge.GetModVersionsAsync(mod.ForgeModId!.Value, SptCompatibility.ForgeRangeConstraint(sptVersion), cancellationToken)).FirstOrDefault();
            results[mod.Key] = new ModUpdateInfo(
                ModUpdateStatus.UnknownVersion,
                latest?.Version,
                latest?.Id,
                latest?.Link,
                latest?.ContentLength,
                $"Version {mod.Version} is not listed on The Forge.");
        }

        if (retry.Count > 0)
        {
            var second = await _forge.CheckUpdatesAsync(retry.Select(m => new ModVersionPair(m.ForgeModId!.Value.ToString(), m.Version!)), sptVersion, cancellationToken);
            ApplyUpdateCheck(second, retry, results);
            _store.Save(_state);
        }

        foreach (var mod in forgeMods.Where(m => !results.ContainsKey(m.Key)))
        {
            results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.UnknownVersion, Reason: $"Version {mod.Version} is not listed on The Forge.");
        }

        // The Forge only offers versions linked to the exact SPT version asked about, but mods made for an earlier
        // patch of the same minor keep working. Checking earlier patches too catches those versions (and clears
        // "incompatible" for mods that simply were not re-tagged for the newest patch).
        var recognized = forgeMods.Where(m => results[m.Key].Status != ModUpdateStatus.UnknownVersion).ToList();
        if (recognized.Count > 0)
        {
            foreach (var earlier in await GetEarlierSptPatchesAsync(cancellationToken))
            {
                ForgeUpdateCheck alternative;
                try
                {
                    alternative = await _forge.CheckUpdatesAsync(recognized.Select(m => new ModVersionPair(m.ForgeModId!.Value.ToString(), m.Version!)), earlier, cancellationToken);
                }
                catch (ForgeApiException)
                {
                    continue;
                }

                var alternativeResults = new Dictionary<string, ModUpdateInfo>();
                ApplyUpdateCheck(alternative, recognized, alternativeResults);
                foreach (var (key, info) in alternativeResults)
                {
                    results[key] = MergeUpdateInfo(results[key], info);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Combines results from checks against different SPT patches: the newest available update wins, and a mod that
    /// is fine on any patch up to the installed one is not reported as incompatible.
    /// </summary>
    internal static ModUpdateInfo MergeUpdateInfo(ModUpdateInfo current, ModUpdateInfo other)
    {
        if (other.Status == ModUpdateStatus.UpdateAvailable)
        {
            return current.Status == ModUpdateStatus.UpdateAvailable && VersionUtil.Compare(current.LatestVersion, other.LatestVersion) >= 0
                ? current
                : other;
        }

        if (current.Status == ModUpdateStatus.IncompatibleWithSpt && other.Status is ModUpdateStatus.UpToDate or ModUpdateStatus.Blocked)
        {
            return other;
        }

        return current;
    }

    private static void ApplyUpdateCheck(ForgeUpdateCheck check, List<InstalledMod> mods, Dictionary<string, ModUpdateInfo> results)
    {
        InstalledMod? Find(int modId) => mods.FirstOrDefault(m => m.ForgeModId == modId);

        foreach (var update in check.Updates)
        {
            if (Find(update.CurrentVersion.ModId) is { } mod)
            {
                var recommended = update.RecommendedVersion;
                results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.UpdateAvailable, recommended.Version, recommended.Id, recommended.Link, recommended.ContentLength);
            }
        }

        foreach (var blocked in check.BlockedUpdates)
        {
            if (Find(blocked.CurrentVersion.ModId) is { } mod)
            {
                var reason = blocked.BlockingMods.Count > 0
                    ? "Held back by " + string.Join(", ", blocked.BlockingMods.Select(b => $"{b.ModName} (needs {b.Constraint})"))
                    : blocked.BlockReason?.Replace('_', ' ');

                results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.Blocked, blocked.LatestVersion?.Version, blocked.LatestVersion?.Id, Reason: reason);
            }
        }

        foreach (var current in check.UpToDate)
        {
            if (Find(current.ModId) is { } mod)
            {
                mod.ForgeVersionId ??= current.Id;
                results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.UpToDate, current.Version, current.Id);
            }
        }

        foreach (var incompatible in check.IncompatibleWithSpt)
        {
            if (Find(incompatible.ModId) is { } mod)
            {
                var latest = incompatible.LatestCompatibleVersion;
                results[mod.Key] = new ModUpdateInfo(ModUpdateStatus.IncompatibleWithSpt, latest?.Version, latest?.Id, latest?.Link, latest?.ContentLength,
                    "No version of this mod is marked compatible with your SPT version.");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    private string RequireSptVersion() =>
        _install.SptVersion ?? throw new InvalidOperationException("Could not read your SPT version, so compatible mods cannot be resolved.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp file cleanup is best effort.
        }
    }
}
