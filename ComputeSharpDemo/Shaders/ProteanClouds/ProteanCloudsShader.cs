using ComputeSharp;

namespace ComputeSharpDemo.Shaders.ProteanClouds;

/// <summary>
/// HLSL port of "Protean Clouds" by nimitz (https://www.shadertoy.com/view/3l23Rh).
/// License: CC BY-NC-SA 3.0.
/// </summary>
[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[GeneratedComputeShaderDescriptor]
public readonly partial struct ProteanCloudsShader(
    float iTime,
    Float2 iMouse,
    Float2 iResolution,
    bool isHdrEnabled,
    float sdrWhiteLevelInNits,
    float maxLuminanceInNits) : IComputeShader<Float4>
{
    private static readonly Float3x3 M3 = new(
         0.33338f * 1.93f, -0.87887f * 1.93f, 0.15162f * 1.93f,
         0.56034f * 1.93f,  0.32651f * 1.93f, 0.69596f * 1.93f,
        -0.71817f * 1.93f, -0.15323f * 1.93f, 0.61339f * 1.93f);

    public Float4 Execute()
    {
        Int2 xy = ThreadIds.XY;
        if (xy.X >= iResolution.X || xy.Y >= iResolution.Y)
            return Float4.Zero;

        Float2 fragCoord = new(xy.X + 0.5f, iResolution.Y - (xy.Y + 0.5f));
        Float2 q = fragCoord / iResolution;
        Float2 p  = (fragCoord - Scale(iResolution, 0.5f)) / iResolution.YY;
        Float2 bsMo = (iMouse  - Scale(iResolution, 0.5f)) / iResolution.YY;

        float time = iTime * 3.0f;
        Float3 ro = new(0.0f, 0.0f, time);
        ro += new Float3(Hlsl.Sin(iTime) * 0.5f, 0.0f, 0.0f);

        const float dspAmp = 0.85f;
        ro.XY = ro.XY + Scale(Disp(ro.Z), dspAmp);
        const float tgtDst = 3.5f;
        Float2 dispAtTgt = Scale(Disp(time + tgtDst), dspAmp);
        Float3 offset = new(dispAtTgt.X, dispAtTgt.Y, time + tgtDst);
        Float3 target = Hlsl.Normalize(ro - offset);
        ro.X -= bsMo.X * 2.0f;

        Float3 rightdir = Hlsl.Normalize(Hlsl.Cross(target, new Float3(0.0f, 1.0f, 0.0f)));
        Float3 updir    = Hlsl.Normalize(Hlsl.Cross(rightdir, target));
        rightdir        = Hlsl.Normalize(Hlsl.Cross(updir, target));
        Float3 rd = Hlsl.Normalize(new Float3(
            p.X * rightdir.X + p.Y * updir.X - target.X,
            p.X * rightdir.Y + p.Y * updir.Y - target.Y,
            p.X * rightdir.Z + p.Y * updir.Z - target.Z));

        Float2x2 rotMat = Rot(-Disp(time + 3.5f).X * 0.2f + bsMo.X);
        rd = new Float3(
            rd.X * rotMat.M11 + rd.Y * rotMat.M21,
            rd.X * rotMat.M12 + rd.Y * rotMat.M22,
            rd.Z);

        float prm1 = Hlsl.SmoothStep(-0.4f, 0.4f, Hlsl.Sin(iTime * 0.3f));
        Float4 scn = Render(ro, rd, time, prm1, bsMo.Y);

        Float3 col = new(scn.X, scn.Y, scn.Z);
        Float3 colBgr = new(scn.Z, scn.Y, scn.X);
        col = ILerp(colBgr, col, Hlsl.Clamp(1.0f - prm1, 0.05f, 1.0f));
        col = new Float3(
            Hlsl.Pow(col.X, 0.55f),
            Hlsl.Pow(col.Y, 0.65f),
            Hlsl.Pow(col.Z, 0.60f)) * new Float3(1.0f, 0.97f, 0.9f);
        col = Scale(col, Hlsl.Pow(16.0f * q.X * q.Y * (1.0f - q.X) * (1.0f - q.Y), 0.12f) * 0.7f + 0.3f);

        if (isHdrEnabled)
        {
            Float3 nits = Hlsl.Min(col * sdrWhiteLevelInNits, maxLuminanceInNits);

            return new Float4(PqEncode(nits), 1.0f);
        }

        return new Float4(Hlsl.Saturate(col.X), Hlsl.Saturate(col.Y), Hlsl.Saturate(col.Z), 1.0f);
    }

    // ST 2084 (PQ) inverse EOTF, mapping linear luminance in nits to [0, 1] signal values
    private static Float3 PqEncode(Float3 linearNits)
    {
        linearNits = Hlsl.Max(linearNits, Float3.Zero);

        Float3 n = linearNits / 10000.0f;
        Float3 y = Hlsl.Pow(n, new Float3(0.1593017578125f, 0.1593017578125f, 0.1593017578125f));
        Float3 num = 0.8359375f + 18.8515625f * y;
        Float3 den = 1.0f + 18.6875f * y;

        return Hlsl.Pow(num / den, new Float3(78.84375f, 78.84375f, 78.84375f));
    }

    private static Float2 Scale(Float2 v, float s) => new(v.X * s, v.Y * s);
    private static Float3 Scale(Float3 v, float s) => new(v.X * s, v.Y * s, v.Z * s);
    private static Float4 Scale(Float4 v, float s) => new(v.X * s, v.Y * s, v.Z * s, v.W * s);

    private static Float2x2 Rot(float a)
    {
        float c = Hlsl.Cos(a);
        float s = Hlsl.Sin(a);
        return new Float2x2(c, -s, s, c);
    }
    private static float LinStep(float mn, float mx, float x) =>
        Hlsl.Clamp((x - mn) / (mx - mn), 0.0f, 1.0f);
    private static Float2 Disp(float t) =>
        Scale(new Float2(Hlsl.Sin(t * 0.22f), Hlsl.Cos(t * 0.175f)), 2.0f);

    private static Float2 Map(Float3 p, float time, float prm1, float bsMoY)
    {
        Float3 p2 = p;
        p2.XY = p2.XY - Disp(p.Z);
        float rotAngle = Hlsl.Sin(p.Z + time) * (0.1f + prm1 * 0.05f) + time * 0.09f;
        p.XY = Hlsl.Mul(p.XY, Rot(rotAngle));
        float cl = Hlsl.Dot(p2.XY, p2.XY);
        float d = 0.0f;
        p = Scale(p, 0.61f);
        float z = 1.0f;
        float trk = 1.0f;
        float dspAmp = 0.1f + prm1 * 0.2f;
        for (int i = 0; i < 5; i++)
        {
            p = p + Scale(Hlsl.Sin(p.ZXY * (0.75f * trk) + time * trk * 0.8f), dspAmp);
            d -= Hlsl.Abs(Hlsl.Dot(Hlsl.Cos(p), Hlsl.Sin(p.YZX)) * z);
            z *= 0.57f;
            trk *= 1.4f;
            p = Hlsl.Mul(M3, p);
        }
        d = Hlsl.Abs(d + prm1 * 3.0f) + prm1 * 0.3f - 2.5f + bsMoY;
        return new Float2(d + cl * 0.2f + 0.25f, cl);
    }

    private static Float4 Render(Float3 ro, Float3 rd, float time, float prm1, float bsMoY)
    {
        Float4 rez = Float4.Zero;
        float t = 1.5f;
        float fogT = 0.0f;
        for (int i = 0; i < 130; i++)
        {
            if (rez.W > 0.99f) break;
            Float3 pos = ro + t * rd;
            Float2 mpv = Map(pos, time, prm1, bsMoY);
            float den = Hlsl.Clamp(mpv.X - 0.3f, 0.0f, 1.0f) * 1.12f;
            float dn  = Hlsl.Clamp(mpv.X + 2.0f, 0.0f, 3.0f);
            Float4 col = Float4.Zero;
            if (mpv.X > 0.6f)
            {
                Float3 c = Hlsl.Sin(
                    new Float3(5.0f, 0.4f, 0.2f) + mpv.Y * 0.1f +
                    Hlsl.Sin(pos.Z * 0.4f) * 0.5f + 1.8f);
                col = new Float4(c * 0.5f + 0.5f, 0.08f);
                col = Scale(col, den * den * den);
                col.XYZ = Scale(col.XYZ, LinStep(4.0f, -2.5f, mpv.X) * 2.3f);
                float dif = Hlsl.Clamp((den - Map(pos + 0.8f,  time, prm1, bsMoY).X) / 9.0f,  0.001f, 1.0f);
                dif += Hlsl.Clamp((den - Map(pos + 0.35f, time, prm1, bsMoY).X) / 2.5f, 0.001f, 1.0f);
                Float3 baseTint = new(0.005f, 0.045f, 0.075f);
                Float3 accent   = new(0.033f, 0.07f,  0.03f);
                col.XYZ = col.XYZ * den * (baseTint + Scale(accent, 1.5f * dif));
            }
            float fogC = Hlsl.Exp(t * 0.2f - 2.2f);
            Float4 fogCol = new(0.06f, 0.11f, 0.11f, 0.1f);
            col = col + fogCol * Hlsl.Clamp(fogC - fogT, 0.0f, 1.0f);
            fogT = fogC;
            rez = rez + col * (1.0f - rez.W);
            t += Hlsl.Clamp(0.5f - dn * dn * 0.05f, 0.09f, 0.3f);
        }
        return Hlsl.Clamp(rez, 0.0f, 1.0f);
    }

    private static float GetSat(Float3 c)
    {
        float mi = Hlsl.Min(Hlsl.Min(c.X, c.Y), c.Z);
        float ma = Hlsl.Max(Hlsl.Max(c.X, c.Y), c.Z);
        return (ma - mi) / (ma + 1e-7f);
    }

    private static Float3 ILerp(Float3 a, Float3 b, float x)
    {
        Float3 ic = Hlsl.Lerp(a, b, x) + new Float3(1e-6f, 0.0f, 0.0f);
        float sd = Hlsl.Abs(GetSat(ic) - Hlsl.Lerp(GetSat(a), GetSat(b), x));
        Float3 dir = Hlsl.Normalize(new Float3(
            2.0f * ic.X - ic.Y - ic.Z,
            2.0f * ic.Y - ic.X - ic.Z,
            2.0f * ic.Z - ic.Y - ic.X));
        float lgt = Hlsl.Dot(new Float3(1.0f, 1.0f, 1.0f), ic);
        float ff = Hlsl.Dot(dir, Hlsl.Normalize(ic));
        ic = ic + Scale(dir, 1.5f * sd * ff * lgt);
        return Hlsl.Clamp(ic, 0.0f, 1.0f);
    }
}

