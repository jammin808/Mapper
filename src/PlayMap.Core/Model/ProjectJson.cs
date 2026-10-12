using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PlayMap.Core.Model;

/// <summary>The project's file and wire form: camel-case JSON, enums as words, every list written (empty as
/// []), read leniently — an older or hand-written file with a list missing reads as empty, never as a
/// broken model. The same options serialise one entity for GET and read one back for SET, so the field
/// paths an AI sees are the ones it writes.</summary>
public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = Create(indented: true);

    public static readonly JsonSerializerOptions Compact = Create(indented: false);

    private static JsonSerializerOptions Create(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize<Project>(json, Options) ?? throw new JsonException("The file holds no project.");
        return Sanitize(p);
    }

    public static string SerializeEntity<T>(T entity, bool indented = false) =>
        JsonSerializer.Serialize(entity, indented ? Options : Compact);

    public static T DeserializeEntity<T>(string json) =>
        Sanitize(JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("No value."));

    /// <summary>Applies one field edit to an entity: serialise, set the dotted camel-case path to the JSON
    /// value, read back (the model's types validate the shape). "content.source" on a surface,
    /// "warp.pin.topLeft" on an output, "items.2.label" for a list element.</summary>
    public static T SetPath<T>(T entity, string path, string jsonValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = JsonSerializer.SerializeToNode(entity, Compact) ?? throw new EditRefusedException("Nothing to edit.");
        JsonNode? value;
        try
        {
            value = JsonNode.Parse(jsonValue);
        }
        catch (JsonException)
        {
            // A bare word reads as a string, so SET sf1 name Lobby works without quotes.
            value = JsonValue.Create(jsonValue);
        }
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        JsonNode node = root;
        for (var i = 0; i < parts.Length - 1; i++)
            node = Child(node, parts[i]) ?? throw new EditRefusedException($"no field '{string.Join('.', parts[..(i + 1)])}'");
        var last = parts[^1];
        switch (node)
        {
            case JsonObject obj:
                var key = obj.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(last, StringComparison.OrdinalIgnoreCase))
                    ?? throw new EditRefusedException($"no field '{path}'");
                obj[key] = value;
                break;
            case JsonArray arr when int.TryParse(last, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx) && idx >= 0 && idx < arr.Count:
                arr[idx] = value;
                break;
            default:
                throw new EditRefusedException($"no field '{path}'");
        }
        try
        {
            return Sanitize(root.Deserialize<T>(Options) ?? throw new EditRefusedException("The value made nothing."));
        }
        catch (JsonException ex)
        {
            throw new EditRefusedException($"'{path}' does not take that value ({ex.Message.Split('.')[0]})");
        }
    }

    private static JsonNode? Child(JsonNode node, string part) => node switch
    {
        JsonObject o => o.FirstOrDefault(kv => kv.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value,
        JsonArray a when int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= 0 && i < a.Count => a[i],
        _ => null,
    };

    /// <summary>Replaces every default (uninitialised) immutable array in a model graph with an empty one,
    /// on clones — the object handed in is never written.</summary>
    public static T Sanitize<T>(T value) => (T)SanitizeObject(value!)!;

    private static object? SanitizeObject(object? value)
    {
        if (value is null)
            return null;
        var type = value.GetType();
        if (type.IsPrimitive || value is string || type.IsEnum || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !IsImmutableArray(type))
            return value;
        if (IsImmutableArray(type))
            return SanitizeArray(value, type);
        if (!type.IsClass || type.GetMethod("<Clone>$") is not { } clone)
            return value;
        object? copy = null;
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                continue;
            var old = prop.GetValue(value);
            var fixedValue = SanitizeObject(old);
            if (!ReferenceEquals(old, fixedValue) && !Equals(old, fixedValue))
            {
                copy ??= clone.Invoke(value, null);
                prop.SetValue(copy, fixedValue);
            }
        }
        return copy ?? value;
    }

    private static bool IsImmutableArray(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ImmutableArray<>);

    private static object SanitizeArray(object value, Type type)
    {
        var isDefault = (bool)type.GetProperty(nameof(ImmutableArray<int>.IsDefault))!.GetValue(value)!;
        var elementType = type.GetGenericArguments()[0];
        if (isDefault)
            return type.GetField(nameof(ImmutableArray<int>.Empty), BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        if (elementType.IsPrimitive || elementType == typeof(string) || elementType.IsValueType)
            return value;
        var items = ((IEnumerable)value).Cast<object?>().ToList();
        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            var f = SanitizeObject(items[i]);
            if (!ReferenceEquals(f, items[i]))
            {
                items[i] = f;
                changed = true;
            }
        }
        if (!changed)
            return value;
        var builder = typeof(ImmutableArray).GetMethods()
            .First(m => m.Name == nameof(ImmutableArray.CreateRange) && m.GetParameters().Length == 1)
            .MakeGenericMethod(elementType);
        var typed = typeof(Enumerable).GetMethod(nameof(Enumerable.Cast))!.MakeGenericMethod(elementType).Invoke(null, [items])!;
        return builder.Invoke(null, [typed])!;
    }
}
