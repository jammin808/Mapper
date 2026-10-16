using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.OpenAL;

namespace PlayMap.Media.Audio;

/// <summary>The OpenAL library this process speaks to, loaded once. It is found the way the runtime finds the app's own
/// native assets (the native folders the host lists from the app's deps — runtimes/&lt;rid&gt;/native, or beside a
/// published exe — then the system's search), so the OpenAL Soft that ships with PlayMap is used when the machine has
/// none. Silk.NET's own resolver is not used: it takes the RID from os-release and maps it to runtimes/linux-x64 only
/// through a runtime graph, which Microsoft's portable runtime does not carry, so on Ubuntu it never finds the bundled
/// library. Loading never throws: when no OpenAL loads, <see cref="Loaded"/> is false and <see cref="Words"/> say why.</summary>
public static class OpenAlLibrary
{
    private static readonly Lazy<State> Value = new(() => Load(CandidateNames()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>An OpenAL library is loaded and both APIs are bound to it.</summary>
    public static bool Loaded => Value.Value.Handle != 0;

    internal static State Current => Value.Value;

    /// <summary>The name of the library that loaded, or why none did.</summary>
    public static string Words => Value.Value.Words;

    internal static ALContext Alc => Value.Value.Alc ?? throw new InvalidOperationException(Words);

    internal static AL Al => Value.Value.Al ?? throw new InvalidOperationException(Words);

    /// <summary>The names tried in order: OpenAL Soft's own file name (the bundled one), then the system OpenAL's.</summary>
    public static IReadOnlyList<string> CandidateNames()
    {
        if (OperatingSystem.IsWindows())
            return ["soft_oal.dll", "OpenAL32.dll"];
        if (OperatingSystem.IsMacOS())
            return ["libopenal.dylib", "libopenal.1.dylib"];
        return ["libopenal.so", "libopenal.so.1"];
    }

    /// <summary>Loads the first of <paramref name="names"/> that loads and exports alcOpenDevice; never throws.
    /// Each call loads afresh (the process uses the one <see cref="Loaded"/> reports).</summary>
    internal static State Load(IReadOnlyList<string> names)
    {
        var refused = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (!NativeLibrary.TryLoad(name, typeof(OpenAlLibrary).Assembly, null, out var handle))
            {
                refused.Add(name + " not found");
                continue;
            }
            if (!NativeLibrary.TryGetExport(handle, "alcOpenDevice", out _))
            {
                NativeLibrary.Free(handle);
                refused.Add(name + " is not OpenAL");
                continue;
            }
            return new State(handle, BindAlc(handle), BindAl(handle), name);
        }
        return new State(0, null, null, "no OpenAL library loads (" + string.Join(", ", refused) + ")");
    }

    // The library's exports first, then (as Silk.NET's GetApi does) the library's own proc lookup for extension functions.
    private static unsafe ALContext BindAlc(nint handle)
    {
        var contexts = new MultiNativeContext(Exports(handle), null);
        var alc = new ALContext(contexts);
        contexts.Contexts[1] = new LamdaNativeContext(x =>
            x.EndsWith("GetProcAddress", StringComparison.Ordinal) || x.EndsWith("GetContextsDevice", StringComparison.Ordinal)
            || x.EndsWith("GetCurrentContext", StringComparison.Ordinal)
                ? 0
                : (nint)alc.GetProcAddress(alc.GetContextsDevice(alc.GetCurrentContext()), x));
        return alc;
    }

    private static AL BindAl(nint handle)
    {
        var contexts = new MultiNativeContext(Exports(handle), null);
        var al = new AL(contexts);
        contexts.Contexts[1] = new LamdaNativeContext(x => x.EndsWith("GetProcAddress", StringComparison.Ordinal) ? 0 : al.GetProcAddress(x));
        return al;
    }

    private static LamdaNativeContext Exports(nint handle) =>
        new(name => NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0);

    internal sealed record State(nint Handle, ALContext? Alc, AL? Al, string Words);
}
