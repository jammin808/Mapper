using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace PlayMap.Core.Tests.Versions;

/// <summary>What a built PE file says of its version, read from the file without loading it: the assembly's version and its two
/// version attributes from the metadata, and the Windows version resource (VS_VERSIONINFO) from the resource section — the
/// fixed four-part file version and the string table's FileVersion and ProductVersion, as Windows' file properties and an
/// installer read them. The same reader works on any machine: the compiler writes the resource whatever it runs on.</summary>
internal sealed record Stamp(Version? AssemblyVersion, string FileVersionAttribute, string InformationalAttribute, VersionResource? Resource)
{
    public static Stamp Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        Version? version = null;
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pe.HasMetadata)
        {
            var md = pe.GetMetadataReader();
            if (md.IsAssembly)
            {
                var assembly = md.GetAssemblyDefinition();
                version = assembly.Version;
                foreach (var handle in assembly.GetCustomAttributes())
                {
                    var attribute = md.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference)
                        continue;
                    var parent = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                    if (parent.Kind != HandleKind.TypeReference)
                        continue;
                    var name = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name);
                    if (name is not ("AssemblyFileVersionAttribute" or "AssemblyInformationalVersionAttribute"))
                        continue;
                    var blob = md.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() == 1)
                        attributes[name] = blob.ReadSerializedString() ?? "";
                }
            }
        }
        return new Stamp(version, attributes.GetValueOrDefault("AssemblyFileVersionAttribute", ""),
            attributes.GetValueOrDefault("AssemblyInformationalVersionAttribute", ""), VersionResource.Read(pe));
    }
}

/// <summary>A PE file's VS_VERSIONINFO: the fixed file version's four parts and the first string table's strings.</summary>
internal sealed record VersionResource(IReadOnlyList<ushort> FileVersionParts, IReadOnlyDictionary<string, string> Strings)
{
    private const int RtVersion = 16;
    private const uint FixedSignature = 0xFEEF04BD;

    public string FixedFileVersion => string.Join('.', FileVersionParts);

    public string String(string key) => Strings.GetValueOrDefault(key, "");

    /// <summary>The resource, or null when the file has none.</summary>
    public static VersionResource? Read(PEReader pe)
    {
        var table = pe.PEHeaders.PEHeader?.ResourceTableDirectory ?? default;
        if (table.Size == 0)
            return null;
        var section = pe.GetSectionData(table.RelativeVirtualAddress).GetContent().AsSpan().ToArray();
        // Three levels: type (RT_VERSION), name, language; the leaf names the data by its RVA.
        var level = Entry(section, 0, RtVersion);
        if (level is not { } typeDir)
            return null;
        var nameDir = Entry(section, typeDir, null);
        var languageLeaf = nameDir is { } n ? Entry(section, n, null) : null;
        if (languageLeaf is not { } leaf)
            return null;
        var dataRva = BitConverter.ToInt32(section.AsSpan(leaf, 4));
        var size = BitConverter.ToInt32(section.AsSpan(leaf + 4, 4));
        var data = pe.GetSectionData(dataRva).GetContent(0, size).AsSpan().ToArray();
        return Parse(data);
    }

    /// <summary>The offset an entry of the resource directory at <paramref name="dir"/> points at (a subdirectory's or a leaf's,
    /// its high bit cleared): the entry with this id, or the first entry when <paramref name="id"/> is null.</summary>
    private static int? Entry(byte[] section, int dir, int? id)
    {
        int named = BitConverter.ToUInt16(section.AsSpan(dir + 12, 2));
        int ids = BitConverter.ToUInt16(section.AsSpan(dir + 14, 2));
        for (var i = 0; i < named + ids; i++)
        {
            var at = dir + 16 + (i * 8);
            var name = BitConverter.ToUInt32(section.AsSpan(at, 4));
            var offset = BitConverter.ToUInt32(section.AsSpan(at + 4, 4));
            if (id is null || ((name & 0x8000_0000u) == 0 && name == (uint)id))
                return (int)(offset & 0x7FFF_FFFFu);
        }
        return null;
    }

    private static VersionResource Parse(byte[] data)
    {
        var root = Block.At(data, 0);
        if (root.Key != "VS_VERSION_INFO" || root.ValueLength < 52 || BitConverter.ToUInt32(data.AsSpan(root.ValueAt, 4)) != FixedSignature)
            throw new InvalidDataException("the version resource does not start with VS_VERSION_INFO and its fixed file info");
        var ms = BitConverter.ToUInt32(data.AsSpan(root.ValueAt + 8, 4));
        var ls = BitConverter.ToUInt32(data.AsSpan(root.ValueAt + 12, 4));
        IReadOnlyList<ushort> parts = [(ushort)(ms >> 16), (ushort)ms, (ushort)(ls >> 16), (ushort)ls];
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var info in root.Children(data).Where(c => c.Key == "StringFileInfo"))
        {
            foreach (var stringTable in info.Children(data).Take(1))
            {
                foreach (var s in stringTable.Children(data))
                    strings[s.Key] = Encoding.Unicode.GetString(data, s.ValueAt, s.ValueBytes).TrimEnd('\0');
            }
        }
        return new VersionResource(parts, strings);
    }

    /// <summary>One block of the resource: its length, its value (WCHARs for text, bytes otherwise), its key, its children after
    /// the value, each aligned to four bytes.</summary>
    private readonly record struct Block(int End, string Key, int ValueAt, int ValueLength, int ValueBytes, int ChildrenAt)
    {
        public static Block At(byte[] data, int start)
        {
            int length = BitConverter.ToUInt16(data.AsSpan(start, 2));
            int valueLength = BitConverter.ToUInt16(data.AsSpan(start + 2, 2));
            var text = BitConverter.ToUInt16(data.AsSpan(start + 4, 2)) == 1;
            var p = start + 6;
            var key = new StringBuilder();
            while (BitConverter.ToUInt16(data.AsSpan(p, 2)) is var c && c != 0)
            {
                key.Append((char)c);
                p += 2;
            }
            p = Align(p + 2);
            var valueBytes = Math.Min(text ? valueLength * 2 : valueLength, Math.Max(0, start + length - p));
            return new Block(start + length, key.ToString(), p, valueLength, valueBytes, Align(p + valueBytes));
        }

        public IEnumerable<Block> Children(byte[] data)
        {
            var p = ChildrenAt;
            while (p + 6 <= End)
            {
                var child = At(data, p);
                if (child.End <= p)
                    yield break;
                yield return child;
                p = Align(child.End);
            }
        }

        private static int Align(int p) => (p + 3) & ~3;
    }
}
