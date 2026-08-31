Texture2D<float> ArenaSdf : register(t1);
SamplerState SdfSampler : register(s1);
static const float SdfMipGradScale = 0.70710678f; // 2^-0.5 => -0.5 LOD bias

cbuffer ArenaSdfConstants : register(b1)
{
    float4 ArenaUvRow0;
    float4 ArenaUvRow1;
};

struct PS_INPUT
{
    float4 pos : SV_POSITION;
};

float arenaSdPx(float2 framebufferPx)
{
    float3 hp = float3(framebufferPx, 1.0f);
    float2 uv = float2(dot(hp, ArenaUvRow0.xyz), dot(hp, ArenaUvRow1.xyz));
    float2 clampedUv = saturate(uv);
    float sampledPx = ArenaSdf.SampleGrad(SdfSampler, clampedUv, float2(ArenaUvRow0.x, ArenaUvRow1.x) * SdfMipGradScale, float2(ArenaUvRow0.y, ArenaUvRow1.y) * SdfMipGradScale).r * ArenaUvRow1.w;
    float2 outsideUv = uv - clampedUv;
    float gradU = max(length(ArenaUvRow0.xy), 1e-7f);
    float gradV = max(length(ArenaUvRow1.xy), 1e-7f);
    float2 outsidePxAxes = float2(outsideUv.x / gradU, outsideUv.y / gradV);
    return sampledPx + length(outsidePxAxes);
}

float4 main(PS_INPUT i) : SV_Target
{
    float d = arenaSdPx(i.pos.xy);
    clip(-d);
    return float4(0.0f, 0.0f, 0.0f, 0.0f);
}
