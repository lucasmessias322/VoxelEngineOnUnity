#ifndef VOXEL_BREAK_INTERACTION_INCLUDED
#define VOXEL_BREAK_INTERACTION_INCLUDED

float4 _VoxelBreakBlockCenterWS;
float4 _VoxelBreakBlockHalfExtents;
float4 _VoxelBreakShake;

float VoxelBreakPhase(float3 centerWS)
{
    return dot(centerWS, float3(12.9898, 78.233, 37.719));
}

float3 VoxelBreakOffset(float3 centerWS, float progress01)
{
    float frequency = max(_VoxelBreakShake.z, 0.001);
    float phase = VoxelBreakPhase(centerWS);
    float t = _Time.y * frequency + phase;
    float3 dir = float3(
        sin(t * 1.31),
        sin(t * 1.73 + 2.0),
        cos(t * 1.11 + 0.6));

    dir = normalize(dir + 1e-5);
    float amplitude = _VoxelBreakShake.y * (0.45 + saturate(progress01) * 0.75);
    return dir * amplitude;
}

float VoxelBreakScale(float3 centerWS, float progress01)
{
    float frequency = max(_VoxelBreakShake.z, 0.001);
    float phase = VoxelBreakPhase(centerWS);
    float t = _Time.y * frequency + phase;
    float pulse = sin(t * 2.6) * 0.5 + 0.5;
    return 1.0 + _VoxelBreakShake.w * (0.4 + saturate(progress01) * 0.6) * (0.65 + pulse * 0.35);
}

void ApplyVoxelBreakInteraction(inout float3 positionWS)
{
    float active = saturate(_VoxelBreakBlockCenterWS.w);
    if (active <= 0.0001)
        return;

    float3 centerWS = _VoxelBreakBlockCenterWS.xyz;
    float3 halfExtents = max(_VoxelBreakBlockHalfExtents.xyz, float3(0.0001, 0.0001, 0.0001));
    float3 inside = step(abs(positionWS - centerWS), halfExtents);
    float vertexMask = inside.x * inside.y * inside.z * active;
    if (vertexMask <= 0.0001)
        return;

    float progress01 = saturate(_VoxelBreakShake.x);
    float scale = VoxelBreakScale(centerWS, progress01);
    float3 offset = VoxelBreakOffset(centerWS, progress01);
    float3 movedPosition = centerWS + (positionWS - centerWS) * scale + offset;
    positionWS = lerp(positionWS, movedPosition, vertexMask);
}

#endif
