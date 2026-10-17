// Test pass of the blood drop flight (BloodFloorGpuTests): the HLSL closed form (blood_flight.hlsli) on given starts - the same cases
// as BloodFlight.Position / Velocity (C#). gPass0 - the cases (float4 pairs: x0, k; v0, t), gPass1 - the output (float4 pairs:
// position; velocity), gPass2 - the number of cases.
#include "common/pass.hlsli"
#include "blood/blood_flight.hlsli"

[numthreads(64, 1, 1)]
void CsFlight(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= gPass2) return;
    StructuredBuffer<float4> cases = ResourceDescriptorHeap[gPass0];
    RWStructuredBuffer<float4> output = ResourceDescriptorHeap[gPass1];
    float4 a = cases[2 * id.x], b = cases[2 * id.x + 1];
    output[2 * id.x] = float4(BloodFlightPosition(a.xyz, b.xyz, a.w, b.w), 0);
    output[2 * id.x + 1] = float4(BloodFlightVelocity(b.xyz, a.w, b.w), 0);
}
