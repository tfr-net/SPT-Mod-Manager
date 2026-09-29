using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SptModManager.Core.Mods;

public sealed record AssemblyInfo(string Name, Version? Version, string? InformationalVersion, string? FileVersion);

public sealed record BepInPluginInfo(string Guid, string Name, string Version);

public sealed record ServerModMetadata(string? Guid, string? Name, string? Author, string? Version, string? SptVersion);

/// <summary>
/// Reads mod identity straight out of .NET assemblies using System.Reflection.Metadata, so no mod code is ever loaded
/// or executed. BepInEx client plugins declare [BepInPlugin(guid, name, version)]; SPT 4.x server mods declare a class
/// implementing IModMetadata whose property initializers are compiled into constructor IL, which is scanned here.
/// </summary>
public static class AssemblyInspector
{
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .ToDictionary(o => o.Value);

    private static readonly string[] ServerMetadataTypeNames = ["IModMetadata", "AbstractModMetadata"];

    public static AssemblyInfo? ReadAssemblyInfo(string path)
    {
        return WithReader(path, (pe, reader) =>
        {
            if (!reader.IsAssembly)
            {
                return null;
            }

            var definition = reader.GetAssemblyDefinition();
            string? informational = null;
            string? fileVersion = null;

            foreach (var handle in definition.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                var name = GetAttributeTypeName(reader, attribute);

                if (name is "AssemblyInformationalVersionAttribute")
                {
                    informational = DecodeStringArguments(attribute).FirstOrDefault();
                }
                else if (name is "AssemblyFileVersionAttribute")
                {
                    fileVersion = DecodeStringArguments(attribute).FirstOrDefault();
                }
            }

            return new AssemblyInfo(reader.GetString(definition.Name), definition.Version, informational, fileVersion);
        });
    }

    public static IReadOnlyList<BepInPluginInfo> ReadBepInPlugins(string path)
    {
        return WithReader(path, (pe, reader) =>
        {
            var plugins = new List<BepInPluginInfo>();

            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                foreach (var attributeHandle in type.GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(attributeHandle);
                    if (GetAttributeTypeName(reader, attribute) != "BepInPlugin")
                    {
                        continue;
                    }

                    var args = DecodeStringArguments(attribute);
                    if (args.Count >= 3 && !string.IsNullOrWhiteSpace(args[0]))
                    {
                        plugins.Add(new BepInPluginInfo(args[0]!, args[1] ?? args[0]!, args[2] ?? string.Empty));
                    }
                }
            }

            return plugins;
        }) ?? [];
    }

    public static ServerModMetadata? ReadServerModMetadata(string path)
    {
        return WithReader(path, (pe, reader) =>
        {
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if ((type.Attributes & TypeAttributes.Abstract) != 0 || !IsServerMetadataType(reader, type))
                {
                    continue;
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var methodName = reader.GetString(method.Name);

                    if (methodName == ".ctor")
                    {
                        ScanConstructor(pe, reader, method, values);
                    }
                }

                // Expression-bodied getters (`public string ModGuid => "..."`) are scanned when the ctor had nothing.
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    var methodName = reader.GetString(method.Name);

                    if (methodName.StartsWith("get_", StringComparison.Ordinal) || methodName.Contains(".get_", StringComparison.Ordinal))
                    {
                        var property = methodName[(methodName.LastIndexOf("get_", StringComparison.Ordinal) + 4)..];
                        if (!values.ContainsKey(property))
                        {
                            ScanGetter(pe, reader, method, property, values);
                        }
                    }
                }

                values.TryGetValue("ModGuid", out var guid);
                if (string.IsNullOrWhiteSpace(guid))
                {
                    continue;
                }

                values.TryGetValue("Name", out var name);
                values.TryGetValue("Author", out var author);
                values.TryGetValue("Version", out var version);
                values.TryGetValue("SptVersion", out var sptVersion);

                return new ServerModMetadata(guid, name, author, version, sptVersion);
            }

            return null;
        });
    }

    private static T? WithReader<T>(string path, Func<PEReader, MetadataReader, T?> action)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
            {
                return default;
            }

            return action(peReader, peReader.GetMetadataReader());
        }
        catch (BadImageFormatException)
        {
            return default;
        }
        catch (InvalidOperationException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static bool IsServerMetadataType(MetadataReader reader, TypeDefinition type)
    {
        foreach (var implementationHandle in type.GetInterfaceImplementations())
        {
            var implementation = reader.GetInterfaceImplementation(implementationHandle);
            if (ServerMetadataTypeNames.Contains(GetTypeName(reader, implementation.Interface)))
            {
                return true;
            }
        }

        return !type.BaseType.IsNil && ServerMetadataTypeNames.Contains(GetTypeName(reader, type.BaseType));
    }

    private static string? GetTypeName(MetadataReader reader, EntityHandle handle)
    {
        return handle.Kind switch
        {
            HandleKind.TypeReference => reader.GetString(reader.GetTypeReference((TypeReferenceHandle)handle).Name),
            HandleKind.TypeDefinition => reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)handle).Name),
            _ => null,
        };
    }

    private static string? GetAttributeTypeName(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle typeHandle = attribute.Constructor.Kind switch
        {
            HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            _ => default,
        };

        var name = typeHandle.IsNil ? null : GetTypeName(reader, typeHandle);
        return name;
    }

    private static IReadOnlyList<string?> DecodeStringArguments(CustomAttribute attribute)
    {
        try
        {
            var value = attribute.DecodeValue(new StringOnlyTypeProvider());
            return value.FixedArguments.Select(a => a.Value as string).ToList();
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Walks constructor IL tracking the constants pushed before each <c>stfld</c>. Property initializers such as
    /// <c>ModGuid { get; init; } = "com.x"</c> compile to <c>ldarg.0; ldstr "com.x"; stfld &lt;ModGuid&gt;k__BackingField</c>,
    /// and <c>Version = new("1.0.0")</c> adds a <c>newobj</c> between the <c>ldstr</c> and the <c>stfld</c>.
    /// </summary>
    private static void ScanConstructor(PEReader peReader, MetadataReader reader, MethodDefinition method, Dictionary<string, string> values)
    {
        var strings = new List<string>();
        var ints = new List<int>();

        foreach (var (opCode, operand) in ReadInstructions(peReader, method))
        {
            if (opCode == OpCodes.Ldstr)
            {
                strings.Add(reader.GetUserString(MetadataTokens.UserStringHandle(operand & 0x00FFFFFF)));
            }
            else if (TryGetInt(opCode, operand, out var intValue))
            {
                ints.Add(intValue);
            }
            else if (opCode == OpCodes.Stfld)
            {
                var property = BackingFieldProperty(GetFieldName(reader, operand));
                if (property is not null && !values.ContainsKey(property))
                {
                    if (strings.Count > 0)
                    {
                        values[property] = strings[^1];
                    }
                    else if (property == "Version" && ints.Count >= 3)
                    {
                        values[property] = $"{ints[0]}.{ints[1]}.{ints[2]}";
                    }
                }

                strings.Clear();
                ints.Clear();
            }
        }
    }

    private static void ScanGetter(PEReader peReader, MetadataReader reader, MethodDefinition method, string property, Dictionary<string, string> values)
    {
        var ints = new List<int>();

        foreach (var (opCode, operand) in ReadInstructions(peReader, method))
        {
            if (opCode == OpCodes.Ldstr)
            {
                values[property] = reader.GetUserString(MetadataTokens.UserStringHandle(operand & 0x00FFFFFF));
                return;
            }

            if (TryGetInt(opCode, operand, out var intValue))
            {
                ints.Add(intValue);
            }

            if (opCode == OpCodes.Ret)
            {
                break;
            }
        }

        if (property == "Version" && ints.Count >= 3)
        {
            values[property] = $"{ints[0]}.{ints[1]}.{ints[2]}";
        }
    }

    private static string? BackingFieldProperty(string? fieldName)
    {
        // Compiler-generated auto-property backing fields are named "<Property>k__BackingField".
        if (fieldName is null || !fieldName.StartsWith('<'))
        {
            return null;
        }

        var end = fieldName.IndexOf(">k__BackingField", StringComparison.Ordinal);
        return end > 1 ? fieldName[1..end] : null;
    }

    private static string? GetFieldName(MetadataReader reader, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
            HandleKind.MemberReference => reader.GetString(reader.GetMemberReference((MemberReferenceHandle)handle).Name),
            _ => null,
        };
    }

    private static bool TryGetInt(OpCode opCode, int operand, out int value)
    {
        value = opCode.Value switch
        {
            0x15 => -1, // ldc.i4.m1
            >= 0x16 and <= 0x1E => opCode.Value - 0x16, // ldc.i4.0 .. ldc.i4.8
            _ => int.MinValue,
        };

        if (value != int.MinValue)
        {
            return true;
        }

        if (opCode == OpCodes.Ldc_I4_S || opCode == OpCodes.Ldc_I4)
        {
            value = operand;
            return true;
        }

        return false;
    }

    private static IEnumerable<(OpCode OpCode, int Operand)> ReadInstructions(PEReader peReader, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0)
        {
            yield break;
        }

        var il = GetIl(peReader, method);
        var offset = 0;

        while (offset < il.Length)
        {
            short value = il[offset++];
            if (value == 0xFE && offset < il.Length)
            {
                value = unchecked((short)(0xFE00 | il[offset++]));
            }

            if (!OpCodesByValue.TryGetValue(value, out var opCode))
            {
                yield break;
            }

            var operand = 0;
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;
                case OperandType.ShortInlineI:
                    operand = (sbyte)il[offset];
                    offset += 1;
                    break;
                case OperandType.InlineVar:
                    offset += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, offset);
                    offset += 4 + (count * 4);
                    break;
                default:
                    // InlineI, InlineBrTarget, InlineField, InlineMethod, InlineSig, InlineString, InlineTok,
                    // InlineType, ShortInlineR are all four bytes.
                    operand = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    break;
            }

            yield return (opCode, operand);
        }
    }

    private static byte[] GetIl(PEReader peReader, MethodDefinition method)
    {
        try
        {
            return peReader.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
        }
        catch (BadImageFormatException)
        {
            return [];
        }
    }

    /// <summary>
    /// Minimal provider that decodes the primitive (string) fixed arguments of custom attributes.
    /// </summary>
    private sealed class StringOnlyTypeProvider : ICustomAttributeTypeProvider<object?>
    {
        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;

        public object? GetSystemType() => null;

        public object? GetSZArrayType(object? elementType) => null;

        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;

        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => null;

        public object? GetTypeFromSerializedName(string name) => null;

        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(object? type) => false;
    }
}
