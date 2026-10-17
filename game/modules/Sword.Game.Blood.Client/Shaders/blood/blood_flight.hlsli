// Flight of a blood drop with linear drag (dv/dt = g - k v): x(t) = x0 + vInf t + (v0 - vInf)(1 - e^(-k t))/k, vInf = g/k - the same
// expressions as BloodFlight.Position / Velocity (C#).
#ifndef SWORD_BLOOD_FLIGHT_HLSLI
#define SWORD_BLOOD_FLIGHT_HLSLI

static const float BLOOD_GRAVITY = 9.81;
static const float BLOOD_MIN_DRAG = 1e-3;

float3 BloodFlightPosition(float3 x0, float3 v0, float k, float t)
{
    k = max(k, BLOOD_MIN_DRAG);
    float3 vInf = float3(0, -BLOOD_GRAVITY / k, 0);
    float e = (1 - exp(-k * t)) / k;
    return x0 + vInf * t + (v0 - vInf) * e;
}

float3 BloodFlightVelocity(float3 v0, float k, float t)
{
    k = max(k, BLOOD_MIN_DRAG);
    float3 vInf = float3(0, -BLOOD_GRAVITY / k, 0);
    return vInf + (v0 - vInf) * exp(-k * t);
}

#endif
