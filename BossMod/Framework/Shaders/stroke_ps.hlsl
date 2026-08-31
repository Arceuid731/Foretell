struct PS_INPUT
{
    float4 pos                         : SV_POSITION;
    float4 col                         : COLOR0;
    float4 shadowCol                   : COLOR1;
    float acrossPx                     : TEXCOORD0;
    float alongPx                      : TEXCOORD1;
    nointerpolation float3 params      : TEXCOORD2;
    nointerpolation uint flags         : TEXCOORD3;
};

float normalizedPixelCoverage(float edgeDistance)
{
    // edgeDistance is expressed in framebuffer pixels. Use derivatives only to recover
    // local edge orientation, then normalize their magnitude away.
    float2 g = float2(ddx(edgeDistance), ddy(edgeDistance));
    float gl = length(g);
    float footprint = gl > 1e-4f ? (abs(g.x) + abs(g.y)) / gl : 1.0f;
    footprint = clamp(footprint, 1.0f, 1.41421356f);
    return saturate(0.5f - edgeDistance / footprint);
}

float strokeCoverage(PS_INPUT input, float halfWidthPx)
{
    float edgeDistance = abs(input.acrossPx) - halfWidthPx;
    if ((input.flags & 0x1u) != 0u)
        edgeDistance = max(edgeDistance, -input.alongPx);
    if ((input.flags & 0x2u) != 0u)
        edgeDistance = max(edgeDistance, input.alongPx - input.params.z);
    return normalizedPixelCoverage(edgeDistance);
}

float4 main(PS_INPUT input) : SV_Target
{
    float colorCoverage = strokeCoverage(input, input.params.x);
    float shadowCoverage = strokeCoverage(input, input.params.y);

    // shadow first, foreground second.
    // Returning their equivalent single straight-alpha source preserves the final blend.
    float ca = input.col.a * colorCoverage;
    float sa = input.shadowCol.a * shadowCoverage;
    float shadowBehind = sa * (1.0f - ca);
    float a = ca + shadowBehind;
    clip(a - 0.001f);
    float3 rgb = (input.col.rgb * ca + input.shadowCol.rgb * shadowBehind) / max(a, 1e-6f);
    return float4(rgb, a);
}
