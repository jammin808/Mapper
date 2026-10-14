using System.Text;

namespace PlayMap.Core.Audio;

/// <summary>Patterns' rule for finding a saved input again: a device's name compared case-insensitively, then by its key — the
/// name with Windows' socket number ("2- " in "Microphone (2- USB Audio CODEC)") removed and spaces folded — so a microphone
/// plugged into another port is the same microphone.</summary>
public static class AudioInputMatch
{
    /// <summary>The input whose name is <paramref name="name"/>, or whose key is the name's key; null when none is.</summary>
    public static AudioInputInfo? Find(IReadOnlyList<AudioInputInfo> inputs, string name)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(name);
        var exact = inputs.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;
        var key = Key(name);
        return key.Length == 0 ? null : inputs.FirstOrDefault(i => Key(i.Name) == key);
    }

    /// <summary>The name without the socket number Windows writes right after a parenthesis ("2- ", "12- "), lower case, spaces
    /// folded.</summary>
    public static string Key(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sb = new StringBuilder(name.Length);
        var i = 0;
        while (i < name.Length)
        {
            if (i > 0 && name[i - 1] == '(' && TrySkipSocket(name, ref i))
                continue;
            var c = name[i++];
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString().Trim();
    }

    private static bool TrySkipSocket(string name, ref int i)
    {
        var j = i;
        while (j < name.Length && char.IsAsciiDigit(name[j]))
            j++;
        if (j == i || j + 1 >= name.Length || name[j] != '-' || name[j + 1] != ' ')
            return false;
        i = j + 2;
        return true;
    }
}
