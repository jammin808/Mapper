using PlayMap.Engine.ShaderWorker;

namespace PlayMap.Render.D3D12.Tests.Shaders;

/// <summary>What the shader worker's DXC reads from an entry's reflection, on both jobs: each cbuffer's size in whole registers,
/// whether a matrix constant is laid one row a register (row_major) or one column a register, and the vertex entry's input
/// signature in its order: each element's semantic (a system value's as DXC names it, in capitals) and index, its system value, its
/// component type and its declared component count.</summary>
public sealed class DxcReflectionTests
{
    private const string Source = """
        cbuffer Program : register(b0)
        {
            row_major float3x3 uRows;
            column_major float4x4 uColumns;
            float uLast;
        };
        struct In
        {
            float3 position : ATTR0;
            float2 uv : ATTR2;
            uint vertex : SV_VertexID;
            uint instance : SV_InstanceID;
        };
        float4 VS(In v) : SV_Position
        {
            return float4(mul(uRows, v.position) + mul(float4(v.uv, 0.0, 1.0), uColumns).xyz + uLast + float(v.vertex + v.instance), 1.0);
        }
        """;

    [Fact]
    public void TheReflectionReadsTheBlocksSizeEachMatrixsOrderAndTheInputSignature()
    {
        var r = HlslCompile.CompileAndReflect(Source, "VS", "vs_6_6", out var words);
        Assert.True(r is not null, words);
        Assert.Equal(new[] { new ReflectedBuffer("Program", 128) }, r.Buffers);
        Assert.Equal(new[] { ("uRows", "float3x3", 0, true), ("uColumns", "float4x4", 48, false), ("uLast", "float", 112, false) },
            r.Constants.Select(c => (c.Name, c.Type, c.Offset, c.RowMajor)));
        Assert.Equal(
            new[]
            {
                new ReflectedInput("ATTR", 0, 0, "float", 3), new ReflectedInput("ATTR", 2, 0, "float", 2),
                new ReflectedInput("SV_VERTEXID", 0, 6, "uint", 1), new ReflectedInput("SV_INSTANCEID", 0, 8, "uint", 1),
            },
            r.Inputs);
    }
}
