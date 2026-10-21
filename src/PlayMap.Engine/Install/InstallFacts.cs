using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using PlayMap.Core.Machine;

namespace PlayMap.Engine.Install;

/// <summary>What the machine says of this node's install (STATE <c>machine.install</c>): installed per machine or run from a folder,
/// its installer's version and folder, whether a supervisor keeps it, the supervisor's status, auto-logon (on, and for whom — never
/// its password), and that the build is unsigned. The Windows edition is the machine platform's (<c>machine.identity.os</c>). Nothing
/// in it is a secret.</summary>
public sealed record InstallFacts(
    bool Installed,
    string Version,
    string Dir,
    bool Supervised,
    string ShowUser,
    SupervisorStatus? Supervisor,
    bool AutoLogon,
    string AutoLogonUser,
    bool Signed)
{
    /// <summary>A run from a folder (or any machine without the install's key).</summary>
    public static readonly InstallFacts NotInstalled = new(false, "", "", false, "", null, false, "", false);

    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteBoolean("installed", Installed);
        w.WriteString("msi", Version);
        w.WriteString("dir", Dir);
        w.WriteBoolean("supervised", Supervised);
        w.WriteString("showUser", ShowUser);
        w.WritePropertyName("supervisor");
        if (Supervisor is { } s)
            s.WriteTo(w);
        else
            w.WriteNullValue();
        w.WriteStartObject("autoLogon");
        w.WriteBoolean("on", AutoLogon);
        w.WriteString("user", AutoLogonUser);
        w.WriteEndObject();
        w.WriteBoolean("signed", Signed);
        w.WriteString("light", Light);
        w.WriteString("words", Words);
        w.WriteEndObject();
    }

    /// <summary>The block's light, as the Supervisor card and preflight's <c>install:supervisor</c> row light the same state: red when
    /// the supervisor stopped trying; amber when a supervised node has no auto-logon, or its supervisor waits (for the session, a
    /// person's resume or the backoff) or is stopping the engine (a person's stop or the freeze rule's); grey for a folder run
    /// (preflight says whether that matters on this node); else green.</summary>
    public string Light => !Installed ? "grey"
        : Supervisor is { State: "stopped-too-many-restarts" } ? "red"
        : Supervised && (!AutoLogon || Supervisor is null || Supervisor.State is "waiting-for-session" or "held" or "backoff" or "stopping") ? "amber"
        : "green";

    /// <summary>The block's words.</summary>
    public string Words
    {
        get
        {
            if (!Installed)
                return "not installed: a folder run, which nothing restarts if it stops";
            var head = string.Create(CultureInfo.InvariantCulture, $"installed {Version} in {Dir}");
            if (!Supervised)
                return head + ", not supervised";
            if (Supervisor is not { } s)
                return head + ": the supervisor has written no status";
            var said = s.State switch
            {
                "stopped-too-many-restarts" => "the supervisor stopped after too many restarts — PlayMap.Supervisor.exe resume",
                "held" => "a person stopped the engine — PlayMap.Supervisor.exe resume",
                "waiting-for-session" => "the supervisor waits for the show user's session",
                _ => string.Create(CultureInfo.InvariantCulture, $"supervised ({s.State}, {s.Restarts} restarts in ten minutes)"),
            };
            return head + ", " + said + (AutoLogon ? "" : "; auto-logon is off");
        }
    }

    /// <summary>Whether a supervisor keeps this machine's engine (HKLM\SOFTWARE\PlayMap, Supervised = 1, which the supervisor's
    /// <c>install</c> writes once its service runs): the desk then attaches to that engine and never starts its own.</summary>
    public static bool SupervisedMachine()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");
        return key?.GetValue("Supervised") is int supervised && supervised != 0;
    }

    /// <summary>This machine's facts: the install's key, the supervisor's status file and auto-logon's user from Windows' registry; a
    /// folder run elsewhere. Auto-logon's password is never read.</summary>
    public static InstallFacts ReadThisMachine()
    {
        if (!OperatingSystem.IsWindows())
            return NotInstalled;
        return ReadWindows(SupervisorStatus.MachineFolder());
    }

    [SupportedOSPlatform("windows")]
    private static InstallFacts ReadWindows(string supervisorFolder)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");
        // A folder run reads nothing more: the block is the same on every machine that has no install.
        if (key is null)
            return NotInstalled;
        using var logon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
        var autoLogon = logon?.GetValue("AutoAdminLogon") as string == "1";
        var autoUser = autoLogon ? logon?.GetValue("DefaultUserName") as string ?? "" : "";
        SupervisorStatus? report = null;
        try
        {
            report = SupervisorStatus.Read(supervisorFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A status file being replaced or not this user's to read: the block says the supervisor wrote none it could read.
            System.Diagnostics.Trace.WriteLine("PlayMap: the supervisor's status could not be read: " + ex.Message);
        }
        return new InstallFacts(true, key.GetValue("Version") as string ?? "", key.GetValue("InstallDir") as string ?? "",
            key.GetValue("Supervised") is int supervised && supervised != 0, key.GetValue("ShowUser") as string ?? "", report, autoLogon, autoUser,
            key.GetValue("Signed") is int signed && signed != 0);
    }
}
