// Flight of a blood drop with linear drag (dv/dt = g - k v): x(t) = x0 + vInf t + (v0 - vInf)(1 - e^(-k t))/k, vInf = g/k, computed
// without cancellation - the same expressions as BloodFlight.Position / Velocity (C#).
#ifndef SWORD_BLOOD_FLIGHT_HLSLI
#define SWORD_BLOOD_FLIGHT_HLSLI

static const float BLOOD_GRAVITY = 9.81;
static const float BLOOD_MIN_DRAG = 1e-3;

// phi(z) = (1 - e^-z) / z, psi(z) = (z - 1 + e^-z) / z^2, e^-z; below z = 0.1 - Taylor series (no cancellation at small drag).
void BloodFlightTerms(float z, out float phi, out float psi, out float decay)
{
    decay = exp(-z);
    if (z < 0.1)
    {
        phi = 1 - z * (0.5 - z * (1.0 / 6 - z * (1.0 / 24)));
        psi = 0.5 - z * (1.0 / 6 - z * (1.0 / 24 - z * (1.0 / 120)));
    }
    else
    {
        phi = (1 - decay) / z;
        psi = (z - 1 + decay) / (z * z);
    }
}

// x(t) = x0 + v0 t phi(kt) + g t^2 psi(kt).
float3 BloodFlightPosition(float3 x0, float3 v0, float k, float t)
{
    k = max(k, BLOOD_MIN_DRAG);
    float phi, psi, decay;
    BloodFlightTerms(k * t, phi, psi, decay);
    return x0 + v0 * (t * phi) + float3(0, -BLOOD_GRAVITY * (t * t * psi), 0);
}

// v(t) = v0 e^-kt + g t phi(kt).
float3 BloodFlightVelocity(float3 v0, float k, float t)
{
    k = max(k, BLOOD_MIN_DRAG);
    float phi, psi, decay;
    BloodFlightTerms(k * t, phi, psi, decay);
    return v0 * decay + float3(0, -BLOOD_GRAVITY * (t * phi), 0);
}

#endif
