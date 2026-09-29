using System.IO.Compression;
using SptModManager.Core.Spt;

namespace SptModManager.Core.Tests.Support;

/// <summary>A throwaway SPT 4.1 install on disk, built from the compiled fixture assemblies.</summary>
public sealed class TestInstall : IDisposable
{
    public TestInstall(string dataFolder = SptInstallation.RuntimeFolder41, bool withServerCore = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "sptmm-tests", Guid.NewGuid().ToString("N"));
        DataFolder = dataFolder;

        Directory.CreateDirectory(Path.Combine(Root, dataFolder, "SPT_Data", "configs"));
        Directory.CreateDirectory(Path.Combine(Root, dataFolder, "user", "mods"));
        Directory.CreateDirectory(Path.Combine(Root, dataFolder, "user", "profiles"));
        Directory.CreateDirectory(Path.Combine(Root, "BepInEx", "plugins", "spt"));
        File.WriteAllText(Path.Combine(Root, "EscapeFromTarkov.exe"), "not really");
        File.WriteAllText(
            Path.Combine(Root, dataFolder, "SPT_Data", "configs", "core.json"),
            """{ "projectName": "SPT", "compatibleTarkovVersion": "0.16.9.40743" }""");

        if (withServerCore)
        {
            File.Copy(Fixture("SPTarkov.Server.Core.dll"), Path.Combine(Root, dataFolder, "SPTarkov.Server.Core.dll"));
        }
    }

    public string Root { get; }

    public string DataFolder { get; }

    public static string Fixture(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    public static string TestData(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    public SptInstallation Detect() => SptInstallation.TryDetect(Root, out var error) ?? throw new InvalidOperationException(error);

    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public void WriteFile(string relative, string content = "x")
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void CopyFixture(string fixtureFileName, string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Fixture(fixtureFileName), path, overwrite: true);
    }

    /// <summary>Creates a zip under the temp root from (entry path, content) pairs. Content "@file" copies a fixture DLL.</summary>
    public string CreateZip(string name, params (string Entry, string Content)[] entries)
    {
        var directory = Path.Combine(Root, "..", "archives-" + Path.GetFileName(Root));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            if (content.StartsWith('@'))
            {
                zip.CreateEntryFromFile(Fixture(content[1..]), entry);
            }
            else
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(content);
            }
        }

        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
            var archives = Path.Combine(Root, "..", "archives-" + Path.GetFileName(Root));
            if (Directory.Exists(archives))
            {
                Directory.Delete(archives, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
