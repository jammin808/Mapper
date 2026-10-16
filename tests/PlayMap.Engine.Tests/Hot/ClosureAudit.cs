using System.Reflection;
using System.Reflection.Emit;

namespace PlayMap.Engine.Tests.Hot;

/// <summary>Reads a method's IL for the closures it constructs: every <c>newobj</c> of a compiler-made display class. An optimised
/// build merges a lambda's scope into the method's when nothing stands between them, so a closure written anywhere in a method is
/// made at its entry, on every call, whichever branch runs; a method on a hot path constructs none and leaves the lambda to a method
/// of its own that runs only when it is needed. The IL read here is the build's own, so the rule holds in either configuration.</summary>
internal static class ClosureAudit
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>The display classes <paramref name="method"/> constructs, by name ([] when it makes no closure).</summary>
    public static IReadOnlyList<string> DisplayClassesMade(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var il = method.GetMethodBody()?.GetILAsByteArray() ?? [];
        var made = new List<string>();
        var typeArgs = method.DeclaringType is { IsGenericType: true } t ? t.GetGenericArguments() : null;
        var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            var code = Codes[value];
            if (code == OpCodes.Newobj)
            {
                var token = BitConverter.ToInt32(il, i);
                if (method.Module.ResolveMethod(token, typeArgs, methodArgs) is { DeclaringType: { } type }
                    && type.Name.Contains("DisplayClass", StringComparison.Ordinal))
                    made.Add(type.Name);
            }
            i += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
        return made;
    }

    /// <summary>The one method of <paramref name="type"/> named <paramref name="name"/>, whatever it takes.</summary>
    public static MethodInfo Method(Type type, string name) =>
        type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == name);

    /// <summary>The one method of <paramref name="type"/> named <paramref name="name"/> taking <paramref name="parameters"/>.</summary>
    public static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, parameters)
        ?? throw new MissingMethodException(type.Name, name);
}
