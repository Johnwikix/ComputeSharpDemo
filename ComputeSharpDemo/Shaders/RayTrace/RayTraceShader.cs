using ComputeSharp;

namespace ComputeSharpDemo.Shaders.RayTrace;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct RayTraceShader(
    float iTime,
    Float2 iMouse,
    Float2 iResolution,
    int frame,
    IReadWriteNormalizedTexture2D<Float4> previousFrame,
    float iDist) : IComputeShader<Float4>
{
    private const int MaxWeight = 100;
    private const int MaxBounces = 10;
    private const int Samples = 1;

    private const float PI = 3.14159265359f;
    private const float PI2 = 6.28318530717f;

    private const int LAMB = 0;
    private const int METAL = 1;
    private const int DIEL = 2;

    private const float Gamma = 2.2f;

    // Sun lighting
    private static readonly Float3 SunDir = Hlsl.Normalize(new Float3(1.0f, 0.8f, -0.5f));
    private static readonly Float3 SunColor = new(3.0f, 2.8f, 2.5f);

    // Sphere 0 — green lambertian
    private static readonly Float3 S0C = new(0, 1, 0);
    private static readonly float S0R = 1.0f;
    private static readonly int S0T = 0;
    private static readonly Float3 S0A = new(0, 0.9f, 0.05f);
    private static readonly float S0P = 0;

    // Sphere 1 — silver metal
    private static readonly Float3 S1C = new(0, 1, 2.5f);
    private static readonly float S1R = 1.0f;
    private static readonly int S1T = 1;
    private static readonly Float3 S1A = new(0.9f, 0.9f, 0.9f);
    private static readonly float S1P = 0.01f;

    // Sphere 2 — glass
    private static readonly Float3 S2C = new(0, 1, -2.5f);
    private static readonly float S2R = 1.0f;
    private static readonly int S2T = 2;
    private static readonly Float3 S2A = new(0, 0, 0);
    private static readonly float S2P = 1.5f;

    // Sphere 3 — glass inner bubble
    private static readonly Float3 S3C = new(0, 1, -2.5f);
    private static readonly float S3R = -0.92f;
    private static readonly int S3T = 2;
    private static readonly Float3 S3A = new(0.9f, 0.9f, 0.9f);
    private static readonly float S3P = 1.5f;

    // Sphere 4 — ground plane (diffuse warm earth)
    private static readonly Float3 S4C = new(0, -1000, 0);
    private static readonly float S4R = 1000.0f;
    private static readonly int S4T = 0;
    private static readonly Float3 S4A = new(0.9f, 0.9f, 0.9f);
    private static readonly float S4P = 0;

    private static bool ShadowHit(Float3 ro, Float3 rd, float tMin, float tMax)
    {
        float t;
        Float3 n;

        if (HitSphere(ro, rd, S0C, S0R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S1C, S1R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S2C, S2R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S3C, S3R, tMin, tMax, out t, out n)) return true;
        if (HitSphere(ro, rd, S4C, S4R, tMin, tMax, out t, out n)) return true;

        return false;
    }

    private static Float2 Scale(Float2 v, float s) => new(v.X * s, v.Y * s);
    private static Float3 Scale(Float3 v, float s) => new(v.X * s, v.Y * s, v.Z * s);
    private static Float4 Scale(Float4 v, float s) => new(v.X * s, v.Y * s, v.Z * s, v.W * s);

    private static float Random(ref float seed, Float2 uv)
    {
        return Hlsl.Frac(Hlsl.Sin(Hlsl.Dot(uv, new Float2(12.9898f, 78.233f)) + seed++) * 43758.5453f);
    }

    private static Float3 RandomUnitVector(ref float seed, Float2 uv)
    {
        float theta = Random(ref seed, uv) * PI2;
        float z = Random(ref seed, uv) * 2.0f - 1.0f;
        float a = Hlsl.Sqrt(1.0f - z * z);
        Float3 vector = new(a * Hlsl.Cos(theta), a * Hlsl.Sin(theta), z);
        return Scale(vector, Hlsl.Sqrt(Random(ref seed, uv)));
    }

    private static Float3 RayPointAt(Float3 origin, Float3 dir, float t)
    {
        return origin + Scale(dir, t);
    }

    private static float Schlick(float cosine, float ior)
    {
        float r0 = (1.0f - ior) / (1.0f + ior);
        r0 *= r0;
        return r0 + (1.0f - r0) * Hlsl.Pow(1.0f - cosine, 5.0f);
    }

    private static bool HitSphere(Float3 ro, Float3 rd,
        Float3 center, float radius,
        float tMin, float tMax,
        out float hitT, out Float3 hitNormal)
    {
        hitT = 0;
        hitNormal = Float3.Zero;

        Float3 oc = ro - center;
        float a = Hlsl.Dot(rd, rd);
        float b = Hlsl.Dot(oc, rd);
        float c = Hlsl.Dot(oc, oc) - radius * radius;
        float d = b * b - a * c;

        if (d <= 0.0001f)
            return false;

        float t = (-b - Hlsl.Sqrt(d)) / a;
        if (t < tMin)
            t = (-b + Hlsl.Sqrt(d)) / a;

        if (t <= tMin || t >= tMax)
            return false;

        hitT = t;
        Float3 p = RayPointAt(ro, rd, t);
        hitNormal = (p - center) / radius;
        return true;
    }

    private static bool HitScene(Float3 ro, Float3 rd, float tMin, float tMax,
        out Float3 position, out Float3 normal,
        out int matType, out Float3 matAlbedo, out float matParam)
    {
        float closest = tMax;
        bool hit = false;
        position = Float3.Zero;
        normal = Float3.Zero;
        matType = 0;
        matAlbedo = Float3.Zero;
        matParam = 0;

        float t;
        Float3 n;

        if (HitSphere(ro, rd, S0C, S0R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S0T; matAlbedo = S0A; matParam = S0P; }

        if (HitSphere(ro, rd, S1C, S1R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S1T; matAlbedo = S1A; matParam = S1P; }

        if (HitSphere(ro, rd, S2C, S2R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S2T; matAlbedo = S2A; matParam = S2P; }

        if (HitSphere(ro, rd, S3C, S3R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S3T; matAlbedo = S3A; matParam = S3P; }

        if (HitSphere(ro, rd, S4C, S4R, tMin, closest, out t, out n))
        { closest = t; hit = true; position = RayPointAt(ro, rd, t); normal = n; matType = S4T; matAlbedo = S4A; matParam = S4P; }

        return hit;
    }

    private static Float3 Refract(Float3 i, Float3 n, float eta)
    {
        float dt = Hlsl.Dot(i, n);
        float k = 1.0f - eta * eta * (1.0f - dt * dt);
        if (k < 0.0f)
            return Float3.Zero;
        return Scale(i * eta - n * (eta * dt + Hlsl.Sqrt(k)), 1.0f);
    }

    private static Float3 GetSkyColor(Float3 dir)
    {
        float t = dir.Y * 0.5f + 0.5f;
        Float3 bottom = new(0.01f, 0.01f, 0.04f);
        Float3 top = new(0.3f, 0.5f, 0.8f);
        Float3 sky = Hlsl.Lerp(bottom, top, t);

        float sunAngle = Hlsl.Max(Hlsl.Dot(dir, SunDir), 0.0f);
        sky += SunColor * Hlsl.Pow(sunAngle, 200.0f) * 0.5f;

        return sky * 0.3f;
    }

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;
        if (xy.X >= iResolution.X || xy.Y >= iResolution.Y)
            return Float4.Zero;

        Float2 fragCoord = new(xy.X + 0.5f, iResolution.Y - (xy.Y + 0.5f));
        Float2 uv = fragCoord / iResolution;
        Float2 pixelSize = new Float2(1.0f, 1.0f) / iResolution;

        float ratio = iResolution.X / iResolution.Y;

        const float fov = 80.0f;
        float halfWidth = Hlsl.Tan(fov * PI / 360.0f);
        float halfHeight = halfWidth / ratio;

        float dist = iDist;
        Float2 mousePos = iMouse / iResolution;
        if (mousePos.X == 0.0f && mousePos.Y == 0.0f)
            mousePos = new Float2(0.55f, 0.2f);

        float cx = Hlsl.Cos(mousePos.X * 10.0f) * dist;
        float cz = Hlsl.Sin(mousePos.X * 10.0f) * dist;
        float cy = mousePos.Y * 10.0f;

        Float3 origin = new(cx, cy, cz);
        Float3 lookAt = new(0.0f, 1.0f, 0.0f);
        Float3 upVector = new(0.0f, 1.0f, 0.0f);

        Float3 w = Hlsl.Normalize(origin - lookAt);
        Float3 u = Hlsl.Cross(upVector, w);
        Float3 v = Hlsl.Cross(w, u);

        Float3 lowerLeft = origin - Scale(u, halfWidth) - Scale(v, halfHeight) - w;
        Float3 horizontal = Scale(u, halfWidth * 2.0f);
        Float3 vertical = Scale(v, halfHeight * 2.0f);

        float seed = iTime;

        Float3 color = Float3.Zero;

        for (int s = 0; s < Samples; s++)
        {
            Float3 dir = lowerLeft - origin;
            dir += Scale(horizontal, pixelSize.X * Random(ref seed, uv) + uv.X);
            dir += Scale(vertical, pixelSize.Y * Random(ref seed, uv) + uv.Y);

            Float3 position, normal, matAlbedo;
            int matType;
            float matParam;

            // Inline trace loop
            Float3 rayRo = origin;
            Float3 rayRd = dir;
            Float3 accum = Float3.Zero;
            Float3 mask = new(1.0f, 1.0f, 1.0f);

            for (int b = 0; b < MaxBounces; b++)
            {
                if (HitScene(rayRo, rayRd, 0.001f, 5000.0f,
                    out position, out normal,
                    out matType, out matAlbedo, out matParam))
                {
                    float ndotl = Hlsl.Dot(normal, SunDir);
                    if (ndotl > 0.0f)
                    {
                        Float3 shadowRo = position + normal * 0.001f;
                        if (!ShadowHit(shadowRo, SunDir, 0.001f, 5000.0f))
                        {
                            if (matType == LAMB)
                            {
                                accum += mask * matAlbedo * SunColor * ndotl * (1.0f / PI);
                            }
                            else if (matType == METAL)
                            {
                                Float3 viewDir = -Hlsl.Normalize(rayRd);
                                Float3 halfVec = Hlsl.Normalize(viewDir + SunDir);
                                float ndoth = Hlsl.Max(Hlsl.Dot(normal, halfVec), 0.0f);
                                float roughness = matParam * matParam + 0.001f;
                                float spec = Hlsl.Pow(ndoth, 1.0f / roughness);
                                accum += mask * matAlbedo * SunColor * spec * ndotl * 0.5f;
                            }
                            else if (matType == DIEL)
                            {
                                Float3 viewDir = -Hlsl.Normalize(rayRd);
                                Float3 halfVec = Hlsl.Normalize(viewDir + SunDir);
                                float ndoth = Hlsl.Max(Hlsl.Dot(normal, halfVec), 0.0f);
                                float fresnel = Schlick(ndoth, matParam);
                                accum += mask * SunColor * Hlsl.Pow(ndoth, 10.0f) * fresnel;
                            }
                        }
                    }

                    if (matType == LAMB)
                    {
                        Float3 d = normal + RandomUnitVector(ref seed, uv);
                        rayRo = position;
                        rayRd = d;
                        mask *= matAlbedo;
                    }
                    else if (matType == METAL)
                    {
                        Float3 reflected = Hlsl.Reflect(rayRd, normal);
                        Float3 d = Scale(RandomUnitVector(ref seed, uv), matParam) + reflected;

                        if (Hlsl.Dot(d, normal) > 0.0f)
                        {
                            rayRo = position;
                            rayRd = d;
                            mask *= matAlbedo;
                        }
                    }
                    else if (matType == DIEL)
                    {
                        Float3 reflected = Hlsl.Reflect(rayRd, normal);
                        Float3 attenuation = new(1.0f, 1.0f, 1.0f);

                        Float3 refracted, outwardNormal;
                        float eta, reflectProb, cosine;

                        float dt = Hlsl.Dot(rayRd, normal);

                        if (dt > 0.0f)
                        {
                            outwardNormal = -normal;
                            eta = matParam;
                            cosine = eta * dt / Hlsl.Length(rayRd);
                        }
                        else
                        {
                            outwardNormal = normal;
                            eta = 1.0f / matParam;
                            cosine = -dt / Hlsl.Length(rayRd);
                        }

                        refracted = Refract(Hlsl.Normalize(rayRd), Hlsl.Normalize(outwardNormal), eta);
                        if (refracted.X != 0.0f || refracted.Y != 0.0f || refracted.Z != 0.0f)
                            reflectProb = Schlick(cosine, matParam);
                        else
                            reflectProb = 1.0f;

                        if (Random(ref seed, uv) < reflectProb)
                        {
                            rayRo = position;
                            rayRd = reflected;
                        }
                        else
                        {
                            rayRo = position;
                            rayRd = refracted;
                        }

                        mask *= attenuation;
                    }
                }
                else
                {
                    Float3 skyColor = GetSkyColor(Hlsl.Normalize(rayRd));
                    skyColor = Hlsl.Pow(skyColor, new Float3(Gamma, Gamma, Gamma));
                    accum += mask * skyColor;
                    break;
                }
            }

            color += accum;
        }

        color /= Samples;

        color = Hlsl.Pow(color, new Float3(1.0f / Gamma, 1.0f / Gamma, 1.0f / Gamma));

        Float4 previousColor = previousFrame[xy];

        float weight = frame < 1 ? 1.0f : Hlsl.Min((float)(frame + 1), (float)MaxWeight);

        Float3 newColor = Hlsl.Lerp(previousColor.RGB, color, 1.0f / weight);

        return new Float4(newColor, 1.0f);
    }
}
