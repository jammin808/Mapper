using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { StoredOnly } },
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        o.MakeReadOnly();
        return o;
    }

    /// <summary>Only what a value is made from is written: a property with no setter and no constructor
    /// parameter of its name is derived (a vector's length, a rectangle's right edge, a lens's throw ratio)
    /// and is left out of files and SET lines.</summary>
    private static void StoredOnly(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
            return;
        var made = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in info.Type.GetConstructors())
            foreach (var p in c.GetParameters())
                if (p.Name is not null)
                    made.Add(p.Name);
        for (var i = info.Properties.Count - 1; i >= 0; i--)
        {
            var p = info.Properties[i];
            var name = (p.AttributeProvider as MemberInfo)?.Name ?? p.Name;
            if (p.Set is null && !made.Contains(name))
                info.Properties.RemoveAt(i);
        }
    }

    public static string Serialize(Project project) => JsonSerializer.Serialize(project, Options);

    public static Project Deserialize(string json) =>
        Sanitize(Read<Project>(ParseTree(json), refuseNulls: false) ?? throw new JsonException("The file holds no project."));

    public static string SerializeEntity<T>(T entity, bool indented = false) =>
        JsonSerializer.Serialize(entity, indented ? Options : Compact);

    public static T DeserializeEntity<T>(string json) =>
        Sanitize(Read<T>(ParseTree(json), refuseNulls: false) ?? throw new JsonException("No value."));

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>A JSON text as a tree, read as the model reader reads text: comments and trailing commas are allowed, keys match
    /// in any case, and a key given twice keeps its last value. A malformed text throws <see cref="JsonException"/>.</summary>
    public static JsonNode? ParseTree(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Copy(JsonElement.Parse(json, DocumentOptions));
    }

    private static JsonNode? Copy(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject(NodeOptions);
                foreach (var prop in element.EnumerateObject())
                {
                    obj.Remove(prop.Name);
                    obj[prop.Name] = Copy(prop.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var list = new JsonArray(NodeOptions);
                foreach (var item in element.EnumerateArray())
                    list.Add(Copy(item));
                return list;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(element, NodeOptions);
        }
    }

    /// <summary>The model read from a JSON tree, after <see cref="DropNullScalars"/>.</summary>
    private static T? Read<T>(JsonNode? node, bool refuseNulls)
    {
        if (node is null)
            return default;
        DropNullScalars(node, Options.GetTypeInfo(typeof(T)), refuseNulls);
        return node.Deserialize<T>(Options);
    }

    /// <summary>A null for a property set after construction whose type cannot hold one (a number, a flag, an enum, a
    /// non-nullable string) is dropped from the tree, so the property keeps its declared default — or, for an edit
    /// (<paramref name="refuseNulls"/>), refused with the field's name; a null for a constructor parameter is left for the
    /// reader to refuse.</summary>
    private static void DropNullScalars(JsonNode node, JsonTypeInfo info, bool refuseNulls)
    {
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object when node is JsonObject obj:
                foreach (var prop in info.Properties)
                {
                    if (!obj.TryGetPropertyValue(prop.Name, out var value))
                        continue;
                    if (value is not null)
                        DropNullScalars(value, Options.GetTypeInfo(prop.PropertyType), refuseNulls);
                    else if (prop.AssociatedParameter is null && CannotHoldNull(prop))
                    {
                        if (refuseNulls)
                            throw new EditRefusedException($"'{prop.Name}' does not take null");
                        obj.Remove(prop.Name);
                    }
                }
                break;
            case JsonTypeInfoKind.Enumerable when node is JsonArray list && info.ElementType is { } elementType:
                var element = Options.GetTypeInfo(elementType);
                foreach (var item in list)
                    if (item is not null)
                        DropNullScalars(item, element, refuseNulls);
                break;
            case JsonTypeInfoKind.Dictionary when node is JsonObject map && info.ElementType is { } valueType:
                var entry = Options.GetTypeInfo(valueType);
                foreach (var (_, item) in map)
                    if (item is not null)
                        DropNullScalars(item, entry, refuseNulls);
                break;
        }
    }

    private static bool CannotHoldNull(JsonPropertyInfo p) =>
        p.PropertyType.IsValueType ? Nullable.GetUnderlyingType(p.PropertyType) is null : p.PropertyType == typeof(string) && !p.IsSetNullable;

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
            value = ParseTree(jsonValue);
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
            return Sanitize(Read<T>(root, refuseNulls: true) ?? throw new EditRefusedException("The value made nothing."));
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

    /// <summary>Replaces every default (uninitialised) immutable array in a model graph with an empty one, every
    /// null sorted dictionary with an empty one, and every null non-nullable nested record with its type's
    /// <c>Default</c>, <c>Empty</c>, <c>None</c> or <c>Auto</c> — on clones; the object handed in is never written. A
    /// property declared nullable (<c>T?</c>) keeps its null.</summary>
    public static T Sanitize<T>(T value) => (T)SanitizeObject(value!)!;

    /// <summary>The names of the static instances a model type offers as "nothing set".</summary>
    private static readonly string[] RestNames = ["Default", "Empty", "None", "Auto"];

    /// <summary>Per property: what a null reads as (<see cref="NoRest"/> when the null stays).</summary>
    private static readonly ConcurrentDictionary<PropertyInfo, object> RestOf = new();

    private static readonly object NoRest = new();

    private static object? RestFor(PropertyInfo prop)
    {
        var rest = RestOf.GetOrAdd(prop, static p =>
        {
            var t = p.PropertyType;
            if (!t.IsClass || new NullabilityInfoContext().Create(p).WriteState != NullabilityState.NotNull)
                return NoRest;
            var isSortedDictionary = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ImmutableSortedDictionary<,>);
            if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !isSortedDictionary)
                return NoRest;
            if (isSortedDictionary)
            {
                var empty = t.GetField(nameof(ImmutableSortedDictionary<int, int>.Empty), BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                return t.GetGenericArguments()[0] == typeof(string)
                    ? t.GetMethod(nameof(ImmutableSortedDictionary<int, int>.WithComparers), [typeof(IComparer<string>)])!.Invoke(empty, [StringComparer.Ordinal])!
                    : empty;
            }
            foreach (var name in RestNames)
                if (t.GetField(name, BindingFlags.Public | BindingFlags.Static) is { IsInitOnly: true } f && f.FieldType == t && f.GetValue(null) is { } v)
                    return v;
            return NoRest;
        });
        return ReferenceEquals(rest, NoRest) ? null : rest;
    }

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
            var fixedValue = old is null ? RestFor(prop) : SanitizeObject(old);
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
