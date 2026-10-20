// PlayMap's product programs in HLSL (Shader Model 6.6), the same programs as the GLSL library with the same constants, and the
// conventions every file keeps:
//
// - Every target is drawn top row first and UV v runs down, as in the GLSL: SV_Position.xy is the pixel with y down, and a
//   texture's v = 0 is its top row. Positions are worked out in the render hardware interface's clip space (ClipSpace: clip
//   y = -w is the target's top row, z from -w to w is depth 0 to 1), the GLSL's own, and every vertex entry ends with PmClip,
//   ClipSpace.ToDirect3D: the rows a draw lands on and its depths are the GL backend's, and a pipeline that culls with
//   FrontCounterClockwise = TRUE and CULL_MODE_BACK culls the triangles GL's back-face cull does.
// - Matrices arrive with their rows one register each (row_major in the block). A float4x4 constant is a row-vector transform
//   (System.Numerics' layout): GLSL's m * v is mul(v, M) here. A float3x3 constant (the normal matrix) is a column-vector
//   transform: GLSL's m * v is mul(M, v) here. A GLSL mat3(...) literal fills columns and HLSL's float3x3(...) rows, so a
//   literal times v is mul(v, float3x3(...)) with the same numbers.
// - Geometry is made from SV_VertexID where the GLSL makes it from gl_VertexID; a mesh's attributes arrive as ATTR<location>.
// - Exact reads are Load; a sampled read uses its slot's sampler (the texture's own filter and wrap, as GL's texture state).
// - GLSL's mod is PmMod (x - y·floor(x / y)), never fmod, whose sign follows x.
//
// The constants are each program's block at b0, generated with the texture slots from the program table (Layout.hlsli, one per
// program, holding PM_PROGRAM_<name> so a family file compiles one program's code at a time). Textures and samplers come through
// the binding block at b1: sixteen texture and sixteen sampler indices into the directly indexed heaps (one root signature for
// every product program).

#ifndef PLAYMAP_COMMON_HLSLI
#define PLAYMAP_COMMON_HLSLI

#include "Layout.hlsli"

cbuffer Bindings : register(b1)
{
    uint4 pmTex[4];
    uint4 pmSmp[4];
};

Texture2D<float4> PmTex(uint slot)
{
    Texture2D<float4> t = ResourceDescriptorHeap[pmTex[slot >> 2][slot & 3]];
    return t;
}

Texture2D<uint4> PmTexU(uint slot)
{
    Texture2D<uint4> t = ResourceDescriptorHeap[pmTex[slot >> 2][slot & 3]];
    return t;
}

SamplerState PmSmp(uint slot)
{
    SamplerState s = SamplerDescriptorHeap[pmSmp[slot >> 2][slot & 3]];
    return s;
}

// texture(s, uv): a sampled read through the slot's sampler.
float4 PmSample(uint slot, float2 uv) { return PmTex(slot).Sample(PmSmp(slot), uv); }

// texelFetch(s, p, 0): an exact read.
float4 PmLoad(uint slot, int2 p) { return PmTex(slot).Load(int3(p, 0)); }

// textureSize(s, 0).
int2 PmSize(uint slot)
{
    uint w, h;
    PmTex(slot).GetDimensions(w, h);
    return int2(w, h);
}

float PmMod(float x, float y) { return x - y * floor(x / y); }
float2 PmMod(float2 x, float2 y) { return x - y * floor(x / y); }
float3 PmMod(float3 x, float3 y) { return x - y * floor(x / y); }

// ClipSpace.ToDirect3D: a clip position as Direct3D rasterises it to the same pixel and depth, y negated (the same row lands top
// row first) and z from -w..w to 0..w.
float4 PmClip(float4 c) { return float4(c.x, -c.y, (c.z + c.w) * 0.5, c.w); }

struct VsUv
{
    float4 pos : SV_Position;
    float2 uv : TEXCOORD0;
};

// A triangle covering the target, from the vertex id; uv runs 0..1 across it, v down.
VsUv FullscreenVS(uint id : SV_VertexID)
{
    float2 p = float2(float((id << 1) & 2), float(id & 2));
    VsUv o;
    o.uv = p;
    o.pos = PmClip(float4(p * 2.0 - 1.0, 0.0, 1.0));
    return o;
}

#endif
