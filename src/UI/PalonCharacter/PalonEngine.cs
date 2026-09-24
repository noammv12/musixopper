using System.Globalization;
using System.Text;

namespace Palon.UI;

// Palon frame engine: a faithful C# port of docs/design/engine.js.
// Motion approach adapted from bloub (MIT, (c) jeremy-prt, github.com/jeremy-prt/bloub):
// pure Sample(t), radial-profile body, eyes on a sphere, easeOutQuint morphs, loopNoise gaze drift, blink schedule.
// Deliberately free of WPF types so it can be unit-tested and diffed against the web engine
// (see tests/Palon.Tests/PalonEngineTests.cs and docs/design/engine.js).

sealed class PalonEyeCfg
{
    public double W, H, Open = 1, Tilt, Bend;
    public PalonEyeCfg Clone() => (PalonEyeCfg)MemberwiseClone();
}

sealed class PalonPose
{
    public double Yaw, Pitch, Roll, Split, Sx = 1, Sy = 1, OffY, Wander, Hair;
    public PalonEyeCfg[] Eyes = { new(), new() };
}

/// <summary>One frame, centred on 0,0 at radius R. Point arrays are interleaved x,y and reused between frames.</summary>
sealed class PalonFrame
{
    public readonly double[] Body = new double[PalonEngine.N * 2];
    public readonly double[] EyeL = new double[PalonEngine.EP * 2];
    public readonly double[] EyeR = new double[PalonEngine.EP * 2];
    /// <summary>3 strands x 5 points (l1, ctrlA, tip, ctrlB, r1) drawn as M l1 Q ctrlA tip Q ctrlB r1 Z.</summary>
    public readonly double[] Hair = new double[3 * 10];
}

/// <summary>Per-avatar animation state (engine.js create()).</summary>
sealed class PalonState
{
    public PalonMood Cur, Prev;
    public bool HasPrev;
    public double TCur, TPrev, BlinkAt = -10, Seed;
    public PalonPose? Frozen;
    public PalonState(PalonMood mood, double now = 0) { Cur = mood; TCur = now; }
}

/// <summary>Extra inputs on top of the web engine. <see cref="Default"/> reproduces engine.js exactly.</summary>
struct PalonExtras
{
    /// <summary>Additive head yaw/pitch in degrees (pointer gaze, "noticed you" glance).</summary>
    public double GazeYaw, GazePitch;
    /// <summary>1 = design motion, 0 = reduced motion (drift, breathing, float, rhythms off; blinks and morphs stay).</summary>
    public double Motion;
    /// <summary>Multiplier on tuft strand width (keeps the tuft readable at tiny sizes).</summary>
    public double HairWidth;
    public static PalonExtras Default => new() { Motion = 1, HairWidth = 1 };
}

static class PalonEngine
{
    public const int N = 64, EP = 44;
    public const double Morph = 0.5;
    const double TAU = Math.PI * 2;
    static readonly double[] COS = new double[N], SIN = new double[N];
    static readonly double[] BL;

    static PalonEngine()
    {
        for (int i = 0; i < N; i++) { COS[i] = Math.Cos((double)i / N * TAU); SIN[i] = Math.Sin((double)i / N * TAU); }
        // blink schedule, deterministic (mulberry32, seed 0x5eed) - bit-identical to the JS Math.imul version
        uint a = 0x5eed;
        double Rnd()
        {
            unchecked
            {
                a += 0x6d2b79f5;
                uint t = (a ^ (a >> 15)) * (1 | a);
                t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
        var o = new List<double>(); double tt = 1.2;
        while (tt < 4000) { o.Add(tt); tt += 2.2 + Rnd() * 2.8; if (Rnd() < 0.18) { o.Add(tt); tt += 0.26; } }
        BL = o.ToArray();
    }

    public static double Clamp(double v, double a = 0, double b = 1) => v < a ? a : v > b ? b : v;
    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Ease(double t) => 1 - Math.Pow(1 - t, 5);
    static double Noise(double t, double p, double s)
    {
        var a = t / p * TAU;
        return 0.55 * Math.Sin(a + s) + 0.3 * Math.Sin(2 * a + s * 1.7 + 1.1) + 0.15 * Math.Sin(3 * a + s * 2.3 + 2.4);
    }

    /// <summary>Lid openness 0..1 from the deterministic blink schedule (1 = open).</summary>
    public static double BlinkLid(double t)
    {
        if (!double.IsFinite(t)) return 1;
        double p = ((t % 4000) + 4000) % 4000; int lo = 0, hi = BL.Length - 1;
        while (lo < hi) { int m = (lo + hi + 1) >> 1; if (BL[m] <= p) lo = m; else hi = m - 1; }
        double k = (p - BL[lo]) / 0.19; if (k < 0 || k > 1) return 1;
        return k < 0.42 ? 1 - k / 0.42 : Ease((k - 0.42) / 0.58);
    }

    /// <summary>True while a scheduled or forced blink is in progress (used to decide whether reduced-motion frames need drawing).</summary>
    public static bool IsBlinking(PalonState s, double now) => BlinkLid(now + s.Seed) < 1 || now - s.BlinkAt < 0.22;

    static PalonEyeCfg Eye(double w, double h, double tilt = 0, double bend = 0) => new() { W = w, H = h, Tilt = tilt, Bend = bend };

    sealed record MoodDef(double Yaw, double Pitch, double Roll, double Split, PalonEyeCfg L, PalonEyeCfg R, double Wander, double Sy = 1);
    // indexed by PalonMood: idle, listen, think, talk, happy
    static readonly MoodDef[] MOODS =
    {
        new(-1, -5.5, -13, 15.5, Eye(.186, .412), Eye(.186, .412), 1),
        new(1, -1, -6, 16, Eye(.215, .46), Eye(.215, .46), .45, 1.012),
        new(-21, 12, -5, 15, Eye(.19, .25, tilt: -6), Eye(.19, .31, tilt: 3), .5),
        new(0, -4, -11, 15.5, Eye(.19, .4), Eye(.19, .4), .6),
        new(0, -3, -9, 17, Eye(.26, .1, bend: .06), Eye(.26, .1, bend: .06), .35),
    };

    /// <summary>Mood pose at local time t. <paramref name="motion"/> scales the rhythmic layers (1 = design).</summary>
    public static PalonPose Mood(PalonMood id, double t, double motion = 1)
    {
        var m = MOODS[(int)id];
        var p = new PalonPose { Yaw = m.Yaw, Pitch = m.Pitch, Roll = m.Roll, Split = m.Split, Sy = m.Sy, Wander = m.Wander };
        p.Eyes[0] = m.L.Clone(); p.Eyes[1] = m.R.Clone();
        switch (id)
        {
            case PalonMood.Talk:
            {
                // syllable rhythm: layered sines, never repeats visibly
                var s = 0.5 + 0.5 * Math.Sin(t * 11.3) * (0.6 + 0.4 * Math.Sin(t * 2.7));
                var s2 = Math.Max(0, Math.Sin(t * 5.9 + 1));
                s = Lerp(0.5, s, motion); s2 *= motion;
                foreach (var e in p.Eyes) { e.H *= 1 + 0.07 * s; e.W *= 1 - 0.025 * s; }
                p.Sy *= 1 + 0.016 * s2; p.Sx *= 1 - 0.008 * s2; p.OffY = -0.014 * s2; p.Pitch += 2.5 * s2; p.Hair = 3 * s2;
                break;
            }
            case PalonMood.Happy:
            {
                double c = (t % 0.95) / 0.95, hop = Math.Sin(Math.PI * c); hop *= hop;
                hop *= motion; var land = Math.Pow(1 - hop, 6) * motion;
                p.OffY = -0.07 * hop; p.Sy *= 1 + 0.025 * hop - 0.03 * land; p.Sx *= 1 - 0.015 * hop + 0.025 * land;
                p.Hair = 10 * Math.Cos(TAU * c) * -1 * motion; p.Roll += 4 * Math.Sin(t * 2.1) * motion;
                break;
            }
            case PalonMood.Think:
                p.Yaw += 5 * Math.Sin(t * 0.9) * motion; p.Pitch += 3 * Math.Sin(t * 0.6 + 1) * motion;
                break;
            case PalonMood.Listen:
            {
                p.Roll += 2 * Math.Sin(t * 1.1) * motion; var nod = Math.Max(0, Math.Sin(t * 2.2)); p.Pitch -= 3 * nod * nod * motion;
                break;
            }
        }
        return p;
    }

    static PalonPose Blend(PalonPose a, PalonPose b, double k)
    {
        var o = new PalonPose
        {
            Yaw = Lerp(a.Yaw, b.Yaw, k), Pitch = Lerp(a.Pitch, b.Pitch, k), Roll = Lerp(a.Roll, b.Roll, k), Split = Lerp(a.Split, b.Split, k),
            Sx = Lerp(a.Sx, b.Sx, k), Sy = Lerp(a.Sy, b.Sy, k), OffY = Lerp(a.OffY, b.OffY, k), Wander = Lerp(a.Wander, b.Wander, k), Hair = Lerp(a.Hair, b.Hair, k),
        };
        for (int i = 0; i < 2; i++)
        {
            PalonEyeCfg e = a.Eyes[i], f = b.Eyes[i];
            o.Eyes[i] = new PalonEyeCfg { W = Lerp(e.W, f.W, k), H = Lerp(e.H, f.H, k), Open = Lerp(e.Open, f.Open, k), Tilt = Lerp(e.Tilt, f.Tilt, k), Bend = Lerp(e.Bend, f.Bend, k) };
        }
        return o;
    }

    public static PalonPose Composed(PalonState s, double now, double motion = 1)
    {
        double since = now - s.TCur;
        var p = Mood(s.Cur, since, motion);
        if (since >= Morph || (!s.HasPrev && s.Frozen == null)) return p;
        var from = s.Frozen ?? Mood(s.Prev, now - s.TPrev, motion);
        return Blend(from, p, Ease(Clamp(since / Morph)));
    }

    public static bool IsMorphing(PalonState s, double now) => now - s.TCur < Morph && (s.HasPrev || s.Frozen != null);

    /// <summary>0.5 s easeOutQuint morph from the pose on screen; an interrupted morph is frozen as the new origin. Forces a blink.</summary>
    public static void SetMood(PalonState s, PalonMood id, double now, double motion = 1)
    {
        if (id == s.Cur || (int)id < 0 || (int)id >= MOODS.Length) return;
        s.Frozen = (s.HasPrev && now - s.TCur < Morph) ? Composed(s, now, motion) : null;
        s.Prev = s.Cur; s.HasPrev = true; s.TPrev = s.TCur; s.Cur = id; s.TCur = now; s.BlinkAt = now;
    }

    // tangent frames of two eyes on a unit sphere (bloub face.ts eyePoses)
    readonly struct V3 { public readonly double X, Y, Z; public V3(double x, double y, double z) { X = x; Y = y; Z = z; } }
    static (V3, V3) Spin(V3 u, V3 v, double a)
    {
        double c = Math.Cos(a), s = Math.Sin(a);
        return (new V3(u.X * c + v.X * s, u.Y * c + v.Y * s, u.Z * c + v.Z * s), new V3(v.X * c - u.X * s, v.Y * c - u.Y * s, v.Z * c - u.Z * s));
    }
    struct EyePose { public double X, Y, A, B, C, D; }
    static void EyePoses(double yaw, double pitch, double roll, double split, Span<EyePose> outp)
    {
        const double d = Math.PI / 180;
        V3 f = new(0, 0, 1), r = new(1, 0, 0), dn = new(0, 1, 0);
        (f, r) = Spin(f, r, yaw * d);
        (dn, f) = Spin(dn, f, pitch * d);
        (r, dn) = Spin(r, dn, roll * d);
        for (int i = 0; i < 2; i++)
        {
            var (e0, e1) = Spin(f, r, split * (i == 0 ? -1 : 1) * d);
            outp[i] = new EyePose { X = e0.X, Y = e0.Y, A = e1.X, B = e1.Y, C = dn.X, D = dn.Y };
        }
    }

    /// <summary>Capsule sampled at EP points evenly by arc length from top centre, clockwise; bend arches it into a ^ arc.</summary>
    static void EyeOutline(double w, double h, double bend, Span<double> outXY)
    {
        double hw = Math.Max(w, .01) / 2, hh = Math.Max(h, .01) / 2, r = Math.Min(hw, hh), ex = hw - r, ey = hh - r, q = Math.PI / 2 * r;
        Span<double> segs = stackalloc double[] { ex, q, 2 * ey, q, 2 * ex, q, 2 * ey, q, ex };
        double total = 0; foreach (var sl in segs) total += sl;
        for (int i = 0; i < EP; i++)
        {
            double d = (double)i / EP * total; int k = 0;
            while (k < segs.Length - 1 && d > segs[k]) { d -= segs[k]; k++; }
            double f = segs[k] > 0 ? d / segs[k] : 0, x, y, a;
            switch (k)
            {
                case 0: x = f * ex; y = -hh; break;
                case 1: a = -Math.PI / 2 + f * Math.PI / 2; x = ex + r * Math.Cos(a); y = -ey + r * Math.Sin(a); break;
                case 2: x = hw; y = -ey + f * 2 * ey; break;
                case 3: a = f * Math.PI / 2; x = ex + r * Math.Cos(a); y = ey + r * Math.Sin(a); break;
                case 4: x = ex - f * 2 * ex; y = hh; break;
                case 5: a = Math.PI / 2 + f * Math.PI / 2; x = -ex + r * Math.Cos(a); y = ey + r * Math.Sin(a); break;
                case 6: x = -hw; y = ey - f * 2 * ey; break;
                case 7: a = Math.PI + f * Math.PI / 2; x = -ex + r * Math.Cos(a); y = -ey + r * Math.Sin(a); break;
                default: x = -ex + f * ex; y = -hh; break;
            }
            double u = x / hw; y -= bend * (1 - u * u) * 1.7;
            outXY[i * 2] = x; outXY[i * 2 + 1] = y;
        }
    }

    public static void Sample(PalonState s, double now, PalonFrame fr, double R = 100) => Sample(s, now, fr, R, PalonExtras.Default);

    /// <summary>engine.js sample(): pure function of (state, now). Writes into <paramref name="fr"/>; allocation-light.</summary>
    public static void Sample(PalonState s, double now, PalonFrame fr, double R, PalonExtras x)
    {
        double mo = x.Motion, hwMul = x.HairWidth <= 0 ? 1 : x.HairWidth;
        var p = Composed(s, now, mo); double w = p.Wander * mo;
        double lid = BlinkLid(now + s.Seed);
        double fk = Clamp((now - s.BlinkAt) / 0.22); if (fk < 1) lid = Math.Min(lid, Math.Abs(fk * 2 - 1));
        double yaw = p.Yaw + (Noise(now, 11.3, .4) * 5.5 + Noise(now, 3.7, 2.1) * 1.6) * w + x.GazeYaw;
        double pitch = p.Pitch + (Noise(now, 9.1, 1.3) * 4 + Noise(now, 4.3, .7) * 1.2) * w + x.GazePitch;
        double roll = p.Roll + Noise(now, 13.7, 3.2) * 2.2 * w;
        double breath = 1 + Math.Sin(now / 3.6 * TAU) * 0.006 * mo;
        double ox = Noise(now, 7.9, 1.9) * 0.006 * mo, oy = p.OffY + Noise(now, 5.3, .3) * 0.007 * mo;
        double sx = p.Sx, sy = p.Sy * breath;
        // body: radial profile, a circle whose squash is anchored at the bottom (feels grounded)
        for (int i = 0; i < N; i++)
        {
            fr.Body[i * 2] = (COS[i] * sx + ox) * R;
            fr.Body[i * 2 + 1] = (SIN[i] * sy + (1 - sy) + oy) * R;
        }
        // eyes
        Span<EyePose> E = stackalloc EyePose[2];
        EyePoses(yaw, pitch, roll, p.Split, E);
        Span<double> outline = stackalloc double[EP * 2];
        for (int k = 0; k < 2; k++)
        {
            var e = E[k]; var cfg = p.Eyes[k]; double ph = cfg.Tilt * Math.PI / 180, cp = Math.Cos(ph), sp = Math.Sin(ph);
            double ax = e.A * cp + e.C * sp, ay = e.B * cp + e.D * sp, bx = -e.A * sp + e.C * cp, by = -e.B * sp + e.D * cp;
            double sq = 0.06 + 0.94 * Clamp(Math.Min(lid, cfg.Open));
            if (cfg.Bend > 0.02) sq = 1 - (1 - sq) * 0.35; // happy arcs barely blink
            double cx = (e.X * sx + ox) * R, cy = (e.Y * sy + (1 - sy) + oy) * R;
            EyeOutline(cfg.W * R, cfg.H * R, cfg.Bend * R, outline);
            var dst = k == 0 ? fr.EyeL : fr.EyeR;
            for (int i = 0; i < EP; i++)
            {
                double qx = outline[i * 2], qy = outline[i * 2 + 1];
                dst[i * 2] = cx + qx * ax + qy * bx;
                dst[i * 2 + 1] = cy + (qx * ay + qy * by) * sq;
            }
        }
        // hair tuft: three tapered strands rooted just inside the top of the body, lagging sway
        double sway = (Noise(now, 4.7, .9) * 6 + Noise(now, 2.3, 2.2) * 2) * mo + p.Hair - roll * 0.6 + yaw * 0.12;
        double tx = (ox + 0.04 + Math.Sin(yaw * Math.PI / 180) * 0.12) * R, ty = (-sy + (1 - sy) + oy + 0.16) * R;
        ReadOnlySpan<double> ST = stackalloc double[] { -16, .28, .17, 6, .41, .19, 28, .26, .15 };
        for (int j = 0; j < 3; j++)
        {
            double ang = (ST[j * 3] + sway * (0.8 + j * 0.25)) * Math.PI / 180, L = ST[j * 3 + 1] * R, wd = ST[j * 3 + 2] * R * hwMul;
            double dx = Math.Sin(ang), dy = -Math.Cos(ang), nx = -dy, ny = dx, bx0 = tx + (j - 1) * 0.045 * R;
            double tipx = bx0 + dx * L + nx * 0.42 * L, tipy = ty + dy * L + ny * 0.42 * L;
            double mx = bx0 + dx * L * 0.55 + nx * 0.2 * L, my = ty + dy * L * 0.55 + ny * 0.2 * L;
            var h = fr.Hair.AsSpan(j * 10, 10);
            h[0] = bx0 - nx * wd / 2; h[1] = ty - ny * wd / 2;
            h[2] = mx - nx * wd * .45; h[3] = my - ny * wd * .45;
            h[4] = tipx; h[5] = tipy;
            h[6] = mx + nx * wd * .55; h[7] = my + ny * wd * .55;
            h[8] = bx0 + nx * wd / 2; h[9] = ty + ny * wd / 2;
        }
    }

    // ---- SVG path strings formatted exactly like engine.js (verification / debugging only) ----
    static string R2(double v)
    {
        var r = Math.Floor(v * 100 + 0.5) / 100; // JS Math.round
        if (r == 0) r = 0; // no "-0"
        return r.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Catmull(double[] P)
    {
        int n = P.Length / 2; var sb = new StringBuilder(n * 40);
        sb.Append('M').Append(R2(P[0])).Append(' ').Append(R2(P[1]));
        for (int i = 0; i < n; i++)
        {
            int i0 = (i - 1 + n) % n, i2 = (i + 1) % n, i3 = (i + 2) % n;
            sb.Append('C').Append(R2(P[i * 2] + (P[i2 * 2] - P[i0 * 2]) / 6)).Append(' ').Append(R2(P[i * 2 + 1] + (P[i2 * 2 + 1] - P[i0 * 2 + 1]) / 6))
              .Append(' ').Append(R2(P[i2 * 2] - (P[i3 * 2] - P[i * 2]) / 6)).Append(' ').Append(R2(P[i2 * 2 + 1] - (P[i3 * 2 + 1] - P[i * 2 + 1]) / 6))
              .Append(' ').Append(R2(P[i2 * 2])).Append(' ').Append(R2(P[i2 * 2 + 1]));
        }
        return sb.Append('Z').ToString();
    }

    public static string HairPath(double[] H)
    {
        var sb = new StringBuilder(300);
        for (int j = 0; j < 3; j++)
        {
            int o = j * 10;
            sb.Append('M').Append(R2(H[o])).Append(' ').Append(R2(H[o + 1]))
              .Append('Q').Append(R2(H[o + 2])).Append(' ').Append(R2(H[o + 3])).Append(' ').Append(R2(H[o + 4])).Append(' ').Append(R2(H[o + 5]))
              .Append('Q').Append(R2(H[o + 6])).Append(' ').Append(R2(H[o + 7])).Append(' ').Append(R2(H[o + 8])).Append(' ').Append(R2(H[o + 9])).Append('Z');
        }
        return sb.ToString();
    }
}
