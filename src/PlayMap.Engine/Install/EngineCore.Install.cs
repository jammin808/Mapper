using System.Text.Json;
using PlayMap.Engine.Install;

namespace PlayMap.Engine;

/// <summary>wp3-installer's hooks into the engine (EngineCore.Modules.cs): the machine block's install key, from the install's
/// registry key and the supervisor's status file, read at most every two seconds.</summary>
public sealed partial class EngineCore
{
    private const long InstallReadEveryMs = 2000;

    private InstallRead? _install;

    /// <summary>Where the install's facts come from: this machine's, or a test's.</summary>
    internal Func<InstallFacts> InstallSource { get; set; } = InstallFacts.ReadThisMachine;

    /// <summary>The install's facts now (read again when two seconds old).</summary>
    internal InstallFacts InstallNow()
    {
        var now = Environment.TickCount64;
        if (Volatile.Read(ref _install) is { } held && now - held.ReadAtMs < InstallReadEveryMs)
            return held.Facts;
        var facts = InstallSource();
        Volatile.Write(ref _install, new InstallRead(facts, now));
        return facts;
    }

    /// <summary>Forgets the facts read, so the next STATE reads them again (a test's changed source).</summary>
    internal void InstallReadAgain() => Volatile.Write(ref _install, null);

    private sealed record InstallRead(InstallFacts Facts, long ReadAtMs);

    partial void MachineInstall(Utf8JsonWriter w)
    {
        w.WritePropertyName("install");
        InstallNow().Write(w);
    }
}
