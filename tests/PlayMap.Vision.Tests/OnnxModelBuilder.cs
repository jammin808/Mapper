using System.Text;

namespace PlayMap.Vision.Tests;

/// <summary>Writes a minimal ONNX model (ModelProto, protobuf wire format) by hand, so the tests run a real
/// ONNX Runtime session on a model they built themselves: inputs, outputs, float/int64 initialisers and nodes
/// with int, ints, float attributes. Field numbers follow onnx.proto (IR version 8, default opset 17).</summary>
internal sealed class OnnxModelBuilder
{
    private const int ElemFloat = 1;
    private const int ElemInt64 = 7;

    private readonly List<byte[]> _nodes = [];
    private readonly List<byte[]> _initialisers = [];
    private readonly List<byte[]> _inputs = [];
    private readonly List<byte[]> _outputs = [];
    private int _nodeCount;

    public OnnxModelBuilder Input(string name, params long[] dims)
    {
        _inputs.Add(ValueInfo(name, ElemFloat, dims));
        return this;
    }

    public OnnxModelBuilder InputInt64(string name, params long[] dims)
    {
        _inputs.Add(ValueInfo(name, ElemInt64, dims));
        return this;
    }

    public OnnxModelBuilder Output(string name, params long[] dims)
    {
        _outputs.Add(ValueInfo(name, ElemFloat, dims));
        return this;
    }

    public OnnxModelBuilder Constant(string name, float[] values, params long[] dims)
    {
        var w = new ProtoWriter();
        foreach (var d in dims)
            w.Varint(1, d);
        w.Varint(2, ElemFloat);
        w.String(8, name);
        var raw = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        w.Bytes(9, raw);
        _initialisers.Add(w.ToArray());
        return this;
    }

    public OnnxModelBuilder ConstantInt64(string name, long[] values, params long[] dims)
    {
        var w = new ProtoWriter();
        foreach (var d in dims)
            w.Varint(1, d);
        w.Varint(2, ElemInt64);
        w.String(8, name);
        var raw = new byte[values.Length * 8];
        Buffer.BlockCopy(values, 0, raw, 0, raw.Length);
        w.Bytes(9, raw);
        _initialisers.Add(w.ToArray());
        return this;
    }

    /// <summary>A node; attributes are (name, value) with value an int, a long[], or a float.</summary>
    public OnnxModelBuilder Node(string op, string[] inputs, string[] outputs, params (string Name, object Value)[] attributes)
    {
        var w = new ProtoWriter();
        foreach (var i in inputs)
            w.String(1, i);
        foreach (var o in outputs)
            w.String(2, o);
        w.String(3, "n" + _nodeCount++);
        w.String(4, op);
        foreach (var (name, value) in attributes)
            w.Message(5, Attribute(name, value));
        _nodes.Add(w.ToArray());
        return this;
    }

    public byte[] Build(int opset = 17)
    {
        var g = new ProtoWriter();
        foreach (var n in _nodes)
            g.Message(1, n);
        g.String(2, "g");
        foreach (var t in _initialisers)
            g.Message(5, t);
        foreach (var i in _inputs)
            g.Message(11, i);
        foreach (var o in _outputs)
            g.Message(12, o);

        var op = new ProtoWriter();
        op.String(1, "");
        op.Varint(2, opset);

        var m = new ProtoWriter();
        m.Varint(1, 8);
        m.String(2, "playmap-tests");
        m.Message(7, g.ToArray());
        m.Message(8, op.ToArray());
        return m.ToArray();
    }

    private static byte[] Attribute(string name, object value)
    {
        var w = new ProtoWriter();
        w.String(1, name);
        switch (value)
        {
            case int i:
                w.Varint(3, i);
                w.Varint(20, 2);
                break;
            case long[] ints:
                foreach (var v in ints)
                    w.Varint(8, v);
                w.Varint(20, 7);
                break;
            case float f:
                w.Fixed32(2, BitConverter.SingleToInt32Bits(f));
                w.Varint(20, 1);
                break;
            default:
                throw new ArgumentException("Unsupported attribute type.", nameof(value));
        }
        return w.ToArray();
    }

    private static byte[] ValueInfo(string name, int elemType, long[] dims)
    {
        var shape = new ProtoWriter();
        foreach (var d in dims)
        {
            var dim = new ProtoWriter();
            if (d >= 0)
                dim.Varint(1, d);
            else
                dim.String(2, "n");
            shape.Message(1, dim.ToArray());
        }
        var tensor = new ProtoWriter();
        tensor.Varint(1, elemType);
        tensor.Message(2, shape.ToArray());
        var type = new ProtoWriter();
        type.Message(1, tensor.ToArray());
        var vi = new ProtoWriter();
        vi.String(1, name);
        vi.Message(2, type.ToArray());
        return vi.ToArray();
    }

    private sealed class ProtoWriter
    {
        private readonly List<byte> _s = [];

        public void Varint(int field, long value)
        {
            Tag(field, 0);
            Raw((ulong)value);
        }

        public void Fixed32(int field, int bits)
        {
            Tag(field, 5);
            _s.AddRange(BitConverter.GetBytes(bits));
        }

        public void String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public void Message(int field, byte[] body) => Bytes(field, body);

        public void Bytes(int field, byte[] body)
        {
            Tag(field, 2);
            Raw((ulong)body.Length);
            _s.AddRange(body);
        }

        public byte[] ToArray() => [.. _s];

        private void Tag(int field, int wire) => Raw((ulong)((field << 3) | wire));

        private void Raw(ulong v)
        {
            while (v >= 0x80)
            {
                _s.Add((byte)(v | 0x80));
                v >>= 7;
            }
            _s.Add((byte)v);
        }
    }
}
