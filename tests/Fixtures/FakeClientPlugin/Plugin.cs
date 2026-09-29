namespace BepInEx
{
    // Minimal stand-ins for the BepInEx types a client plugin references.
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class BepInPlugin(string GUID, string Name, string Version) : Attribute
    {
        public string GUID { get; } = GUID;

        public string Name { get; } = Name;

        public string Version { get; } = Version;
    }

    public abstract class BaseUnityPlugin;
}

namespace FakeClientPlugin
{
    [BepInEx.BepInPlugin("com.test.client", "Test Client Plugin", "1.2.3")]
    public sealed class Plugin : BepInEx.BaseUnityPlugin;
}
