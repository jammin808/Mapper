using System.Globalization;

namespace PlayMap.Core.Effects;

/// <summary>Where an expression's $variables come from: $WIDTH and $HEIGHT (the render size) and $&lt;input&gt; (an input's
/// value). Read on the render thread: answering must not allocate.</summary>
public interface IIsfVariables
{
    bool TryGet(string name, out double value);
}

/// <summary>A pass's WIDTH or HEIGHT expression, parsed once and evaluated every frame without allocating: numbers,
/// $WIDTH, $HEIGHT, $&lt;input&gt;, + − * / % ^, parentheses, and floor ceil round trunc min max pow sqrt abs log exp. A result
/// that is not finite or not positive evaluates to 1, and the caller reports it.</summary>
public sealed class IsfExpression
{
    private enum Op : byte
    {
        Number,
        Variable,
        Negate,
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
        Power,
        Call,
    }

    private sealed class Node(Op op, double value, string name, Node[] args)
    {
        public Op Op { get; } = op;
        public double Value { get; } = value;
        public string Name { get; } = name;
        public Node[] Args { get; } = args;
    }

    private readonly Node? _root;

    private IsfExpression(string text, Node? root, string? error)
    {
        Text = text;
        _root = root;
        Error = error;
    }

    public string Text { get; }

    /// <summary>Why the text did not parse, or null.</summary>
    public string? Error { get; }

    /// <summary>An empty expression: the render size itself.</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>Parses an expression; one that does not parse keeps its error and evaluates to the fallback.</summary>
    public static IsfExpression Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return new IsfExpression("", null, null);
        var parser = new Parser(t);
        try
        {
            var root = parser.Expression();
            parser.SkipSpace();
            if (!parser.AtEnd)
                return new IsfExpression(t, null, $"'{t}' has more after its end, at '{t[parser.Position..]}'");
            return new IsfExpression(t, root, null);
        }
        catch (FormatException ex)
        {
            return new IsfExpression(t, null, $"'{t}' does not read: {ex.Message}");
        }
    }

    /// <summary>The value for these variables; <paramref name="fallback"/> when the expression is empty or does not parse, and 1
    /// (with <paramref name="clamped"/> set) when it comes out not finite or not positive.</summary>
    public int Evaluate(IIsfVariables variables, int fallback, out bool clamped)
    {
        ArgumentNullException.ThrowIfNull(variables);
        clamped = false;
        if (_root is null)
            return fallback;
        var v = Eval(_root, variables);
        if (!double.IsFinite(v) || v < 1)
        {
            clamped = true;
            return 1;
        }
        return (int)Math.Min(16384, Math.Floor(v));
    }

    private static double Eval(Node n, IIsfVariables vars)
    {
        switch (n.Op)
        {
            case Op.Number:
                return n.Value;
            case Op.Variable:
                return vars.TryGet(n.Name, out var v) ? v : double.NaN;
            case Op.Negate:
                return -Eval(n.Args[0], vars);
            case Op.Add:
                return Eval(n.Args[0], vars) + Eval(n.Args[1], vars);
            case Op.Subtract:
                return Eval(n.Args[0], vars) - Eval(n.Args[1], vars);
            case Op.Multiply:
                return Eval(n.Args[0], vars) * Eval(n.Args[1], vars);
            case Op.Divide:
                return Eval(n.Args[0], vars) / Eval(n.Args[1], vars);
            case Op.Modulo:
                return Eval(n.Args[0], vars) % Eval(n.Args[1], vars);
            case Op.Power:
                return Math.Pow(Eval(n.Args[0], vars), Eval(n.Args[1], vars));
            default:
                return Call(n, vars);
        }
    }

    private static double Call(Node n, IIsfVariables vars)
    {
        var a = Eval(n.Args[0], vars);
        return n.Name switch
        {
            "floor" => Math.Floor(a),
            "ceil" => Math.Ceiling(a),
            "round" => Math.Round(a, MidpointRounding.AwayFromZero),
            "trunc" => Math.Truncate(a),
            "sqrt" => Math.Sqrt(a),
            "abs" => Math.Abs(a),
            "log" => Math.Log(a),
            "exp" => Math.Exp(a),
            "min" => Math.Min(a, Eval(n.Args[1], vars)),
            "max" => Math.Max(a, Eval(n.Args[1], vars)),
            "pow" => Math.Pow(a, Eval(n.Args[1], vars)),
            _ => double.NaN,
        };
    }

    /// <summary>Recursive descent: sum := product (('+'|'-') product)*; product := power (('*'|'/'|'%') power)*;
    /// power := unary ('^' power)?; unary := ('-'|'+') unary | atom.</summary>
    private sealed class Parser(string text)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        public void SkipSpace()
        {
            while (Position < text.Length && char.IsWhiteSpace(text[Position]))
                Position++;
        }

        private bool Take(char c)
        {
            SkipSpace();
            if (Position < text.Length && text[Position] == c)
            {
                Position++;
                return true;
            }
            return false;
        }

        public Node Expression()
        {
            var left = Product();
            while (true)
            {
                if (Take('+'))
                    left = new Node(Op.Add, 0, "", [left, Product()]);
                else if (Take('-'))
                    left = new Node(Op.Subtract, 0, "", [left, Product()]);
                else
                    return left;
            }
        }

        private Node Product()
        {
            var left = Power();
            while (true)
            {
                if (Take('*'))
                    left = new Node(Op.Multiply, 0, "", [left, Power()]);
                else if (Take('/'))
                    left = new Node(Op.Divide, 0, "", [left, Power()]);
                else if (Take('%'))
                    left = new Node(Op.Modulo, 0, "", [left, Power()]);
                else
                    return left;
            }
        }

        private Node Power()
        {
            var b = Unary();
            return Take('^') ? new Node(Op.Power, 0, "", [b, Power()]) : b;
        }

        private Node Unary()
        {
            if (Take('-'))
                return new Node(Op.Negate, 0, "", [Unary()]);
            return Take('+') ? Unary() : Atom();
        }

        private Node Atom()
        {
            SkipSpace();
            if (AtEnd)
                throw new FormatException("it ends where a value should be");
            if (Take('('))
            {
                var inner = Expression();
                if (!Take(')'))
                    throw new FormatException("a '(' is never closed");
                return inner;
            }
            var c = text[Position];
            if (c == '$')
            {
                Position++;
                var name = Word();
                if (name.Length == 0)
                    throw new FormatException("'$' needs a name after it");
                return new Node(Op.Variable, 0, name, []);
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = Position;
                while (Position < text.Length && (char.IsDigit(text[Position]) || text[Position] == '.'))
                    Position++;
                if (Position < text.Length && text[Position] is 'e' or 'E')
                {
                    Position++;
                    if (Position < text.Length && text[Position] is '+' or '-')
                        Position++;
                    while (Position < text.Length && char.IsDigit(text[Position]))
                        Position++;
                }
                var s = text[start..Position];
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new FormatException($"'{s}' is not a number");
                return new Node(Op.Number, d, "", []);
            }
            var fn = Word();
            if (fn.Length == 0)
                throw new FormatException($"'{c}' cannot stand here");
            var arity = Arity(fn);
            if (arity < 0)
                throw new FormatException($"there is no function {fn}");
            if (!Take('('))
                throw new FormatException($"{fn} needs ( after it");
            var args = new Node[arity];
            for (var i = 0; i < arity; i++)
            {
                if (i > 0 && !Take(','))
                    throw new FormatException($"{fn} takes {arity} values");
                args[i] = Expression();
            }
            if (!Take(')'))
                throw new FormatException($"{fn} takes {arity} values");
            return new Node(Op.Call, 0, fn, args);
        }

        private static int Arity(string name) => name switch
        {
            "floor" or "ceil" or "round" or "trunc" or "sqrt" or "abs" or "log" or "exp" => 1,
            "min" or "max" or "pow" => 2,
            _ => -1,
        };

        private string Word()
        {
            var start = Position;
            while (Position < text.Length && (char.IsLetterOrDigit(text[Position]) || text[Position] == '_'))
                Position++;
            return text[start..Position];
        }
    }
}
