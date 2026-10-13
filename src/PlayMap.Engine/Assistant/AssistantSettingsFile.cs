using System.Text.Json;
using System.Text.Json.Serialization;
using PlayMap.Assistant;

namespace PlayMap.Engine.Assistant;

/// <summary>The assistant's settings on a node: <see cref="FileName"/> in the node's folder, beside its own settings
/// and as machine-local (the model's key is the credential store's and is never in it). No file: the assistant is
/// off (<see cref="AssistantSettings.Default"/>). A file that does not read is an error, never a silent default: an
/// operator who wrote one meant it, and the node says why it runs without the assistant.</summary>
public static class AssistantSettingsFile
{
    public const string FileName = "playmap.assistant.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string PathIn(NodeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return Path.Combine(home.Folder, FileName);
    }

    /// <summary>The node's assistant settings, normalised.</summary>
    /// <exception cref="InvalidDataException">The file is there and does not read.</exception>
    public static AssistantSettings Load(NodeHome home)
    {
        var path = PathIn(home);
        if (!File.Exists(path))
            return AssistantSettings.Default;
        try
        {
            return (JsonSerializer.Deserialize<AssistantSettings>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{FileName} holds no settings")).Normalised();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the assistant's settings in {FileName} do not read: {ex.Message}", ex);
        }
    }

    /// <summary>Writes the settings (beside, then moved over).</summary>
    public static void Save(NodeHome home, AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = PathIn(home);
        var temp = path + ".saving";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalised(), Json));
        File.Move(temp, path, overwrite: true);
    }
}
