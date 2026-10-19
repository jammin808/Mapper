using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using PlayMap.Core.Storage;

namespace PlayMap.Engine.Install;

/// <summary>The supervisor's status as its file says it (<c>%ProgramData%\PlayMap\supervisor\status.json</c>, written by
/// PlayMap.Supervisor): its state, the engine's pid, restarts within the window, the last cause, the freeze rule's words, when.</summary>
public sealed record SupervisorReport(string State, int Pid, int Restarts, string LastCause, string FreezeRule, long AtUnixMs)
{
    /// <summary>A status file's text read back, or null when it is not one.</summary>
    public static SupervisorReport? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 16 * 1024)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var p)
                || !r.TryGetProperty("restarts", out var restarts) || !restarts.TryGetInt32(out var n)
                || !r.TryGetProperty("at", out var at) || !at.TryGetInt64(out var t))
                return null;
            return new SupervisorReport(state.GetString() ?? "", p, n, Text(r, "lastCause"), Text(r, "freezeRule"), t);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Text(JsonElement r, string name) =>
        r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>What the machine says of this node's install (STATE <c>machine.install</c>): installed per machine or run from a folder,
/// its installer's version and folder, whether a supervisor keeps it, the supervisor's status, auto-logon (on, and for whom — never
/// its password), the Windows edition, and that the build is unsigned. Nothing in it is a secret.</summary>
public sealed record InstallFacts(
    bool Installed,
    string Version,
    string Dir,
    bool Supervised,
    string ShowUser,
    SupervisorReport? Supervisor,
    bool AutoLogon,
    string AutoLogonUser,
    string Edition,
    bool Signed)
{
    /// <summary>A run from a folder (or any machine without the install's key).</summary>
    public static readonly InstallFacts NotInstalled = new(false, "", "", false, "", null, false, "", "", false);

    /// <summary>Whether this edition honours Windows Update's client policies (Home does not).</summary>
    public bool EditionHasUpdatePolicies => !Edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    public void Write(Utf8JsonWriter w)
    {
        ArgumentNullException.ThrowIfNull(w);
        w.WriteStartObject();
        w.WriteBoolean("installed", Installed);
        w.WriteString("msi", Version);
        w.WriteString("dir", Dir);
        w.WriteBoolean("supervised", Supervised);
        w.WriteString("showUser", ShowUser);
        if (Supervisor is { } s)
        {
            w.WriteStartObject("supervisor");
            w.WriteString("state", s.State);
            w.WriteNumber("pid", s.Pid);
            w.WriteNumber("restarts", s.Restarts);
            w.WriteString("lastCause", s.LastCause);
            w.WriteString("freezeRule", s.FreezeRule);
            w.WriteNumber("at", s.AtUnixMs);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("supervisor");
        }
        w.WriteStartObject("autoLogon");
        w.WriteBoolean("on", AutoLogon);
        w.WriteString("user", AutoLogonUser);
        w.WriteEndObject();
        w.WriteString("edition", Edition);
        w.WriteBoolean("signed", Signed);
        w.WriteString("light", Light);
        w.WriteString("words", Words);
        w.WriteEndObject();
    }

    /// <summary>The block's light: red when the supervisor stopped trying, amber when a supervised node has no auto-logon or its
    /// supervisor waits, grey for a folder run (preflight says whether that matters on this node), else green.</summary>
    public string Light => !Installed ? "grey"
        : Supervisor is { State: "stopped-too-many-restarts" } ? "red"
        : Supervised && (!AutoLogon || Supervisor is null || Supervisor.State is "waiting-for-session" or "held" or "backoff") ? "amber"
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

    /// <summary>The supervisor's folder on this machine.</summary>
    public static string SupervisorFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMap", "supervisor");

    /// <summary>This machine's facts: the install's key, the supervisor's status file and auto-logon's user from Windows' registry; a
    /// folder run elsewhere. Auto-logon's password is never read.</summary>
    public static InstallFacts ReadThisMachine()
    {
        if (!OperatingSystem.IsWindows())
            return NotInstalled;
        return ReadWindows(SupervisorFolder());
    }

    [SupportedOSPlatform("windows")]
    private static InstallFacts ReadWindows(string supervisorFolder)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\PlayMap");
        using var logon = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon");
        using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var autoLogon = logon?.GetValue("AutoAdminLogon") as string == "1";
        var autoUser = autoLogon ? logon?.GetValue("DefaultUserName") as string ?? "" : "";
        var edition = current?.GetValue("EditionID") as string ?? "";
        if (key is null)
            return NotInstalled with { AutoLogon = autoLogon, AutoLogonUser = autoUser, Edition = edition };
        var status = Path.Combine(supervisorFolder, "status.json");
        SupervisorReport? report = null;
        try
        {
            report = File.Exists(status) ? SupervisorReport.Parse(DurableFile.ReadAllText(status)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A status file being replaced or not this user's to read: the block says the supervisor wrote none it could read.
            System.Diagnostics.Trace.WriteLine("PlayMap: the supervisor's status could not be read: " + ex.Message);
        }
        return new InstallFacts(true, key.GetValue("Version") as string ?? "", key.GetValue("InstallDir") as string ?? "",
            key.GetValue("Supervised") is int supervised && supervised != 0, key.GetValue("ShowUser") as string ?? "", report, autoLogon, autoUser,
            edition, key.GetValue("Signed") is int signed && signed != 0);
    }
}
