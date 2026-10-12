using System.Globalization;

namespace PlayMap.Assistant;

/// <summary>A time an operator says ("8pm", "20:00", "+5m") as an instant on the node's clock, for a verb's
/// AT &lt;unix ms&gt;. A time of day is the next one to come in the node's time zone (one already past today
/// is tomorrow); an offset counts from now. Nothing else is guessed: "8" alone is refused.</summary>
public static class TimeWords
{
    public static bool TryResolve(string? text, DateTimeOffset now, TimeZoneInfo zone, out DateTimeOffset at, out string why)
    {
        ArgumentNullException.ThrowIfNull(zone);
        at = now;
        why = "";
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0)
        {
            why = "resolve_time needs a time: 20:00, 8pm, 8:15pm, +90s, +5m or +1h.";
            return false;
        }
        if (t == "now")
            return true;
        if (t[0] == '+')
            return TryOffset(t[1..].Trim(), now, out at, out why);
        return TryTimeOfDay(t, now, zone, out at, out why);
    }

    private static bool TryOffset(string t, DateTimeOffset now, out DateTimeOffset at, out string why)
    {
        at = now;
        why = "";
        var unitStart = 0;
        while (unitStart < t.Length && (char.IsAsciiDigit(t[unitStart]) || t[unitStart] == '.'))
            unitStart++;
        var unit = t[unitStart..].Trim();
        if (unitStart == 0 || !double.TryParse(t[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n))
        {
            why = "An offset is a number and a unit: +90s, +5m, +1h.";
            return false;
        }
        TimeSpan span;
        switch (unit)
        {
            case "ms":
                span = TimeSpan.FromMilliseconds(n);
                break;
            case "s" or "sec" or "secs" or "second" or "seconds":
                span = TimeSpan.FromSeconds(n);
                break;
            case "m" or "min" or "mins" or "minute" or "minutes":
                span = TimeSpan.FromMinutes(n);
                break;
            case "h" or "hr" or "hrs" or "hour" or "hours":
                span = TimeSpan.FromHours(n);
                break;
            default:
                why = "An offset needs a unit: +90s, +5m, +1h.";
                return false;
        }
        if (span > TimeSpan.FromDays(2))
        {
            why = "An offset reaches at most two days ahead.";
            return false;
        }
        at = now + span;
        return true;
    }

    private static bool TryTimeOfDay(string t, DateTimeOffset now, TimeZoneInfo zone, out DateTimeOffset at, out string why)
    {
        at = now;
        why = "A time of day is 20:00, 20:00:30, 8pm or 8:15pm.";
        var meridiem = "";
        if (t.EndsWith("am", StringComparison.Ordinal) || t.EndsWith("pm", StringComparison.Ordinal))
        {
            meridiem = t[^2..];
            t = t[..^2].Trim().TrimEnd('.').Trim();
        }
        var parts = t.Split(':');
        if (parts.Length is < 1 or > 3 || (parts.Length == 1 && meridiem.Length == 0))
            return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }
        var (hour, minute, second) = (numbers[0], numbers[1], numbers[2]);
        if (minute > 59 || second > 59)
            return false;
        if (meridiem.Length > 0)
        {
            if (hour is < 1 or > 12)
                return false;
            hour = meridiem == "am" ? hour % 12 : hour % 12 + 12;
        }
        else if (hour > 23)
            return false;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        for (var day = 0; day <= 1; day++)
        {
            var date = local.Date.AddDays(day);
            var wall = new DateTime(date.Year, date.Month, date.Day, hour, minute, second, DateTimeKind.Unspecified);
            var candidate = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (candidate > now)
            {
                at = candidate;
                why = "";
                return true;
            }
        }
        return false;
    }
}
