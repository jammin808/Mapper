namespace PlayMap.Devices.Drivers;

/// <summary>The journal's lane for device lines. A line never carries a password, a digest or a key: drivers
/// write command bodies and outcomes, never handshake bytes.</summary>
public interface IDeviceLog
{
    void Write(string line);
}

public static class DeviceLog
{
    /// <summary>A log that drops every line.</summary>
    public static readonly IDeviceLog None = new NullLog();

    /// <summary>A log that hands each line to <paramref name="sink"/>.</summary>
    public static IDeviceLog To(Action<string> sink) => new SinkLog(sink);

    /// <summary>Removes <paramref name="secret"/> from <paramref name="text"/> — a last fence behind the rule that
    /// no secret is ever put in a line. Secrets shorter than three characters are not searched for (they would
    /// blank ordinary words); the rule still holds for them.</summary>
    public static string Redact(string text, string? secret)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret) || secret.Length < 3)
            return text ?? "";
        return text.Replace(secret, "•••", StringComparison.Ordinal);
    }

    private sealed class NullLog : IDeviceLog
    {
        public void Write(string line)
        {
            // Dropped on purpose: a driver built without a journal writes nowhere.
        }
    }

    private sealed class SinkLog(Action<string> sink) : IDeviceLog
    {
        public void Write(string line) => sink(line);
    }
}
