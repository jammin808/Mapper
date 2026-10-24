using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PlayMap.Engine.ShaderWorker;

/// <summary>A compiler's native library: the file on each platform, and the SHA-256 of the one build PlayMap loads there.</summary>
public sealed record NativePin(string Name, string File, string Sha256, string From);

/// <summary>The compiler natives the shader worker loads, each pinned by hash: glslang and SPIRV-Tools inside shaderc
/// (Silk.NET.Shaderc.Native 2.23.0, built by Silk.NET at its commit 9460514), SPIRV-Cross (Silk.NET.SPIRV.Cross.Native 2.23.0), DXC
/// 1.9.2609's open-source compiler, dxcompiler, alone — its built-in validator validates DXIL and writes its hash, so DXC's separate
/// validator library is never loaded, shipped or pinned — (dxcompiler.dll from Microsoft.Direct3D.DXC 1.9.2609.5 on Windows,
/// libdxcompiler.so from DXC's Linux release on Linux), and on Windows SPIRV-Tools' own library, whose shaderc build exports no
/// validator there (Google's SPIRV-Tools build for Visual Studio 2022, fetched by CI at its hash). A file that is not the pinned
/// build is never loaded: the stage that needs it fails with words. Only the worker process loads any of them; the engine never
/// does.</summary>
public static class CompilerNatives
{
    public const string Shaderc = "shaderc";
    public const string SpirvTools = "spirv-tools";
    public const string SpirvCross = "spirv-cross";
    public const string Dxc = "dxc";

    /// <summary>The pins of the platform this process runs on (empty where PlayMap carries no compiler: a platform other than
    /// Linux x64 and Windows x64).</summary>
    public static ImmutableArray<NativePin> Pins { get; } = PinsFor(RuntimeInformation.RuntimeIdentifier);

    /// <summary>The file names every pinned native has, on any platform: what the engine's process must never hold.</summary>
    public static ImmutableArray<string> AllFiles { get; } =
    [
        "libshaderc_shared.so", "shaderc_shared.dll", "libspirv-cross.so", "spirv-cross.dll", "libdxcompiler.so", "dxcompiler.dll",
        "SPIRV-Tools-shared.dll",
    ];

    /// <summary>The pins for a runtime identifier: linux-x64 or win-x64 (and the forms the runtime reports them by).</summary>
    public static ImmutableArray<NativePin> PinsFor(string rid)
    {
        ArgumentNullException.ThrowIfNull(rid);
        var windows = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase);
        var x64 = rid.EndsWith("-x64", StringComparison.OrdinalIgnoreCase);
        if (!x64)
            return [];
        if (windows)
        {
            return
            [
                new(Shaderc, "shaderc_shared.dll", "8e13592eac48b94755e4aa4c41552b210b41ca906d0bfe35b0a6d47a97d0b7c6", "Silk.NET.Shaderc.Native 2.23.0"),
                new(SpirvTools, "SPIRV-Tools-shared.dll", "288dd59cbd1473b02927950b076016641b59e9e3aa7d5c029f7e0c896397c0bb", "SPIRV-Tools (Google's windows-vs2022-amd64-release build 261)"),
                new(SpirvCross, "spirv-cross.dll", "1b00f8c042d4eb4060b1f4b2c1ee1ef2f0935c7857efe60c62d9cea236ea834e", "Silk.NET.SPIRV.Cross.Native 2.23.0"),
                new(Dxc, "dxcompiler.dll", "0689e2fee0a1c9df8662a59680469f24e2a3ad7ce6bbbcc9284f7a83172bfc59", "Microsoft.Direct3D.DXC 1.9.2609.5 (DXC 1.9.2609)"),
            ];
        }
        return
        [
            // shaderc's Linux build exports SPIRV-Tools' C interface, so the validator is the same library.
            new(Shaderc, "libshaderc_shared.so", "9f2d5c6f53c191e59376251c614043019c6a7e325809703cad186da9b9dce6d3", "Silk.NET.Shaderc.Native 2.23.0"),
            new(SpirvTools, "libshaderc_shared.so", "9f2d5c6f53c191e59376251c614043019c6a7e325809703cad186da9b9dce6d3", "Silk.NET.Shaderc.Native 2.23.0"),
            new(SpirvCross, "libspirv-cross.so", "513de25783914c2193577e36c5623543eaf970b4e8c5abb1ed97e002aa5f874c", "Silk.NET.SPIRV.Cross.Native 2.23.0"),
            new(Dxc, "libdxcompiler.so", "d40e4a7b3b46490313f4bfd08101946483a673831b3ea65c93d746de1c2f9216",
                "DXC 1.9.2609's Linux release linux_dxc_2026_09_28.x86_x64.tar.gz, copied beside the engine by its build from the folder PLAYMAP_DXC_DIR names"),
        ];
    }

    /// <summary>Where a native's file is: beside the assemblies (a published exe), or under runtimes/&lt;rid&gt;/native (a build). Null
    /// when neither holds it.</summary>
    public static string? Locate(NativePin pin, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var root = baseDirectory ?? AppContext.BaseDirectory;
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        foreach (var path in new[] { Path.Combine(root, pin.File), Path.Combine(root, "runtimes", rid, "native", pin.File) })
            if (File.Exists(path))
                return path;
        return null;
    }

    /// <summary>The file's SHA-256 in lowercase hex.</summary>
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Loads a pinned native by name: found, its hash the pin's, then loaded. False with words otherwise (the file is not
    /// there, is not the pinned build, or the system refuses it).</summary>
    public static bool TryLoad(string name, out nint library, out string words, string? baseDirectory = null)
    {
        library = 0;
        if (Pins.FirstOrDefault(p => p.Name == name) is not { } pin)
        {
            words = $"PlayMap carries no {name} for {RuntimeInformation.RuntimeIdentifier}";
            return false;
        }
        if (Locate(pin, baseDirectory) is not { } path)
        {
            words = $"{pin.File} is not beside the worker (it comes from {pin.From})";
            return false;
        }
        string hash;
        try
        {
            hash = HashOf(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            words = $"{pin.File} cannot be read: {ex.Message}";
            return false;
        }
        if (!string.Equals(hash, pin.Sha256, StringComparison.Ordinal))
        {
            words = $"{pin.File} is not the pinned build of {pin.From}: its SHA-256 is {hash}";
            return false;
        }
        if (!NativeLibrary.TryLoad(path, out library))
        {
            words = $"{pin.File} did not load on this machine";
            return false;
        }
        words = "";
        return true;
    }
}
