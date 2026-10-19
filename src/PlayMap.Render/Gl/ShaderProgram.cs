using System.Numerics;
using PlayMap.Core.Gpu;

namespace PlayMap.Render.Gl;

/// <summary>A linked shader program with its uniform locations cached by name and the last value set at
/// each scalar and vector location remembered, so a frame that sets an unchanged uniform costs a dictionary
/// probe and no GL call. Programs are shared objects: every context sharing the device's uses them.
///
/// A uniform is a slot of the program, found by name once (<see cref="SlotOf"/>); the command list's draws set slots by their
/// index, the name-based calls by their name, and both read and write the one remembered value, so the two ways of setting a
/// uniform never disagree about what the GL holds.</summary>
public sealed class ShaderProgram : IDisposable
{
    private struct Slot
    {
        public int Location;
        public bool HasValue;
        public Vector4 Value;
        public int I0;
        public int I1;
        public int I2;
        public int I3;
    }

    private readonly GlDevice _device;
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private Slot[] _slots = new Slot[8];
    private int _count;

    internal ShaderProgram(GlDevice device, uint id, string name)
    {
        _device = device;
        Id = id;
        Name = name;
    }

    internal uint Id { get; private set; }

    public string Name { get; }

    /// <summary>The uniform's location (−1 when the program has no such active uniform; setting it is then a
    /// no-op, which is what GL does with −1 too).</summary>
    public int Location(string name) => _slots[SlotOf(name)].Location;

    public void Set(string name, float x) => SetVectorAt(SlotOf(name), new Vector4(x, 0, 0, 0), 1);

    public void Set(string name, float x, float y) => SetVectorAt(SlotOf(name), new Vector4(x, y, 0, 0), 2);

    public void Set(string name, float x, float y, float z) => SetVectorAt(SlotOf(name), new Vector4(x, y, z, 0), 3);

    public void Set(string name, float x, float y, float z, float w) => SetVectorAt(SlotOf(name), new Vector4(x, y, z, w), 4);

    public void Set(string name, Vector4 v) => SetVectorAt(SlotOf(name), v, 4);

    public void SetInt(string name, int value) => SetIntsAt(SlotOf(name), value, 0, 0, 0, 1);

    /// <summary>A 4×4 matrix in System.Numerics' row-vector layout, uploaded as-is: GLSL's column-major
    /// <c>m * v</c> then computes exactly <c>v * M</c>.</summary>
    public void Set(string name, in Matrix4x4 m) => SetMatrixAt(SlotOf(name), m);

    public void Set(string name, in Matrix3x3 m) => SetMatrixAt(SlotOf(name), m);

    /// <summary>Forgets the remembered values (after another context or a raw call may have changed them).</summary>
    public void ForgetValues()
    {
        for (var i = 0; i < _count; i++)
            _slots[i].HasValue = false;
    }

    /// <summary>The slot of a uniform by name, its location read from the GL the first time the name is asked (an array element by
    /// its own name, <c>uColors[3]</c>).</summary>
    internal int SlotOf(string name)
    {
        if (_index.TryGetValue(name, out var i))
            return i;
        if (_count == _slots.Length)
            Array.Resize(ref _slots, _count * 2);
        i = _count++;
        _slots[i] = new Slot { Location = _device.UniformLocation(Id, name) };
        _index[name] = i;
        return i;
    }

    /// <summary>Sets a float uniform of <paramref name="components"/> (1–4) at a slot, skipped when the slot holds the value.</summary>
    internal void SetVectorAt(int slot, Vector4 v, int components)
    {
        ref var s = ref _slots[slot];
        if (s.Location < 0 || (s.HasValue && s.Value == v))
            return;
        s.HasValue = true;
        s.Value = v;
        _device.UseProgram(this);
        _device.UniformVector(s.Location, v, components);
    }

    /// <summary>Sets an int uniform of <paramref name="components"/> (1, 2 or 4) at a slot, skipped when the slot holds the value.</summary>
    internal void SetIntsAt(int slot, int x, int y, int z, int w, int components)
    {
        ref var s = ref _slots[slot];
        if (s.Location < 0 || (s.HasValue && s.I0 == x && s.I1 == y && s.I2 == z && s.I3 == w))
            return;
        s.HasValue = true;
        (s.I0, s.I1, s.I2, s.I3) = (x, y, z, w);
        _device.UseProgram(this);
        _device.UniformInts(s.Location, x, y, z, w, components);
    }

    /// <summary>Sets a 4×4 matrix at a slot (always: matrices are not remembered).</summary>
    internal void SetMatrixAt(int slot, in Matrix4x4 m)
    {
        var location = _slots[slot].Location;
        if (location < 0)
            return;
        _device.UseProgram(this);
        _device.UniformMatrix4(location, m);
    }

    /// <summary>Sets a 3×3 matrix at a slot (always: matrices are not remembered).</summary>
    internal void SetMatrixAt(int slot, in Matrix3x3 m)
    {
        var location = _slots[slot].Location;
        if (location < 0)
            return;
        _device.UseProgram(this);
        _device.UniformMatrix3(location, m);
    }

    public void Dispose()
    {
        if (Id == 0)
            return;
        _device.DeleteProgram(Id);
        Id = 0;
    }
}
