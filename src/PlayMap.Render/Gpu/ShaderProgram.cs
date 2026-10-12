using System.Numerics;
using System.Runtime.InteropServices;

namespace PlayMap.Render.Gpu;

/// <summary>A linked shader program with its uniform locations cached by name and the last value set at
/// each scalar and vector location remembered, so a frame that sets an unchanged uniform costs a dictionary
/// probe and no GL call. Programs are shared objects: every context sharing the device's uses them.</summary>
public sealed class ShaderProgram : IDisposable
{
    private struct Slot
    {
        public int Location;
        public bool HasValue;
        public Vector4 Value;
        public int IntValue;
    }

    private readonly GlDevice _device;
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

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
    public int Location(string name) => SlotFor(name).Location;

    public void Set(string name, float x) => SetVector(name, new Vector4(x, 0, 0, 0), 1);

    public void Set(string name, float x, float y) => SetVector(name, new Vector4(x, y, 0, 0), 2);

    public void Set(string name, float x, float y, float z) => SetVector(name, new Vector4(x, y, z, 0), 3);

    public void Set(string name, float x, float y, float z, float w) => SetVector(name, new Vector4(x, y, z, w), 4);

    public void Set(string name, Vector4 v) => SetVector(name, v, 4);

    public void SetInt(string name, int value)
    {
        ref var slot = ref SlotRef(name);
        if (slot.Location < 0 || (slot.HasValue && slot.IntValue == value))
            return;
        slot.HasValue = true;
        slot.IntValue = value;
        _device.UseProgram(this);
        _device.UniformInt(slot.Location, value);
    }

    /// <summary>A 4×4 matrix in System.Numerics' row-vector layout, uploaded as-is: GLSL's column-major
    /// <c>m * v</c> then computes exactly <c>v * M</c>.</summary>
    public void Set(string name, in Matrix4x4 m)
    {
        var loc = SlotFor(name).Location;
        if (loc < 0)
            return;
        _device.UseProgram(this);
        _device.UniformMatrix4(loc, m);
    }

    public void Set(string name, in Matrix3x3 m)
    {
        var loc = SlotFor(name).Location;
        if (loc < 0)
            return;
        _device.UseProgram(this);
        _device.UniformMatrix3(loc, m);
    }

    /// <summary>Forgets the remembered values (after another context or a raw call may have changed them).</summary>
    public void ForgetValues()
    {
        foreach (var key in _slots.Keys)
            CollectionsMarshal.GetValueRefOrNullRef(_slots, key).HasValue = false;
    }

    private void SetVector(string name, Vector4 v, int components)
    {
        ref var slot = ref SlotRef(name);
        if (slot.Location < 0 || (slot.HasValue && slot.Value == v))
            return;
        slot.HasValue = true;
        slot.Value = v;
        _device.UseProgram(this);
        _device.UniformVector(slot.Location, v, components);
    }

    private Slot SlotFor(string name) => SlotRef(name);

    private ref Slot SlotRef(string name)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_slots, name, out var existed);
        if (!existed)
        {
            slot.Location = _device.UniformLocation(Id, name);
            slot.HasValue = false;
        }
        return ref slot;
    }

    public void Dispose()
    {
        if (Id == 0)
            return;
        _device.DeleteProgram(Id);
        Id = 0;
    }
}

/// <summary>A 3×3 matrix for normals, row-major (row r, column c at index 3r + c).</summary>
public readonly record struct Matrix3x3(float M00, float M01, float M02, float M10, float M11, float M12, float M20, float M21, float M22)
{
    public static readonly Matrix3x3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
}
