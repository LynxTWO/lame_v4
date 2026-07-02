using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// Transient artifact analyzer for the ABX follow-up. Given an ORIGINAL wav and two encoded-
// then-decoded wavs (A and B), it finds sharp onsets ("k"-like plosives) in the original and,
// for each, compares A and B on the two things a listener conflates as "sharpness":
//   PRE-ECHO  = error energy in the ~15 ms *before* the attack (the classic MP3 transient
//               artifact: a "spit/tick" ahead of the sound). Lower = cleaner.
//   HF        = high-frequency (>10 kHz) energy *at* the attack, vs the original. If a version
//               has much less HF than the original, it is dulling real detail (not just noise).
// This tells us whether A's "sharper" is added pre-echo (artifact) and whether B's smoother
// "texture" costs real high-frequency detail.
static class Program
{
    const int SR = 44100;

    static int Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: transient <orig.wav> <A.wav> <B.wav>"); return 2; }
        var o = ReadMono(args[0]); var a = ReadMono(args[1]); var b = ReadMono(args[2]);
        int oa = BestOffset(o, a), ob = BestOffset(o, b);

        // Onset detection on the original: 512-sample frames, hop 128; onset = energy jump.
        int win = 512, hop = 128;
        var en = new List<double>(); var pos = new List<int>();
        for (int p = 0; p + win <= o.Length; p += hop)
        {
            double e = 0; for (int i = 0; i < win; i++) e += (double)o[p + i] * o[p + i];
            en.Add(e); pos.Add(p);
        }
        var onsets = new List<int>();
        for (int k = 3; k < en.Count - 3; k++)
        {
            double prev = Math.Max(en[k - 1], 1e-9);
            if (en[k] > 6.0 * prev && en[k] > 0.02)  // >~8 dB jump, above a floor
            {
                if (onsets.Count == 0 || pos[k] - onsets[onsets.Count - 1] > SR / 8) onsets.Add(pos[k]);
            }
        }

        // Rank onsets by strength (peak energy) and report the top ones.
        onsets.Sort((x, y) => 0);
        var ranked = new List<(int p, double e)>();
        foreach (var onp in onsets)
        {
            double e = 0; for (int i = 0; i < win && onp + i < o.Length; i++) e += (double)o[onp + i] * o[onp + i];
            ranked.Add((onp, e));
        }
        ranked.Sort((x, y) => y.e.CompareTo(x.e));

        Console.WriteLine("time(s)   preEcho_A  preEcho_B   (lower=cleaner)    HF_orig  HF_A   HF_B   (dB rel)");
        int preSamps = (int)(0.015 * SR); // 15 ms pre-window
        double sumPreA = 0, sumPreB = 0; double sumHfErrA = 0, sumHfErrB = 0; int n = 0;
        int shown = 0;
        foreach (var (p, _) in ranked)
        {
            if (shown >= 12) break;
            double preA = PreEcho(o, a, oa, p, preSamps);
            double preB = PreEcho(o, b, ob, p, preSamps);
            double hfO = HfEnergyDb(o, p);
            double hfA = HfEnergyDb(a, p + oa);
            double hfB = HfEnergyDb(b, p + ob);
            sumPreA += preA; sumPreB += preB;
            sumHfErrA += Math.Abs(hfA - hfO); sumHfErrB += Math.Abs(hfB - hfO); n++;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,7:F3}   {1,9:F2}  {2,9:F2}                      {3,6:F1}  {4,5:F1}  {5,5:F1}",
                (double)p / SR, preA, preB, hfO, hfA - hfO, hfB - hfO));
            shown++;
        }
        Console.WriteLine("----");
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "MEAN pre-echo (dB):     A={0:F2}  B={1:F2}   -> {2}",
            sumPreA / Math.Max(1, n), sumPreB / Math.Max(1, n),
            (sumPreB < sumPreA) ? "B cleaner (less pre-echo)" : "A cleaner (less pre-echo)"));
        // HF fidelity = mean |HF - HF_orig| at transients (lower = closer to original HF detail).
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "MEAN HF error (dB):     A={0:F2}  B={1:F2}   -> {2}",
            sumHfErrA / Math.Max(1, n), sumHfErrB / Math.Max(1, n),
            (sumHfErrB < sumHfErrA) ? "B closer to original HF (crisper transients)" : "A closer to original HF"));
        return 0;
    }

    // Pre-echo = RMS(decoded - original) in the window just before the onset, in dBFS.
    static double PreEcho(float[] orig, float[] dec, int off, int onset, int preSamps)
    {
        int s = onset - preSamps; if (s < 0) s = 0;
        double err = 0; int c = 0;
        for (int i = s; i < onset; i++)
        {
            int di = i + off; if (di < 0 || di >= dec.Length) continue;
            double d = dec[di] - orig[i]; err += d * d; c++;
        }
        if (c == 0) return -120;
        return 10 * Math.Log10(err / c + 1e-12);
    }

    // Energy above 10 kHz in a 1024-window at the onset, in dB.
    static double HfEnergyDb(float[] x, int p)
    {
        int N = 1024; if (p + N >= x.Length) p = Math.Max(0, x.Length - N - 1);
        var re = new double[N]; var im = new double[N];
        for (int i = 0; i < N; i++) { double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1)); re[i] = x[p + i] * w; im[i] = 0; }
        Fft(re, im);
        int k10 = (int)(10000.0 * N / SR);
        double e = 0; for (int k = k10; k < N / 2; k++) e += re[k] * re[k] + im[k] * im[k];
        return 10 * Math.Log10(e + 1e-12);
    }

    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++) { int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit; if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); } }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len, wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len) { double cr = 1, ci = 0; for (int k = 0; k < len / 2; k++) { int aa = i + k, bb = i + k + len / 2; double xr = re[bb] * cr - im[bb] * ci, xi = re[bb] * ci + im[bb] * cr; re[bb] = re[aa] - xr; im[bb] = im[aa] - xi; re[aa] += xr; im[aa] += xi; double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t; } }
        }
    }

    static int BestOffset(float[] a, float[] b, int maxOff = 3000)
    {
        int win = Math.Min(1 << 15, Math.Min(a.Length, b.Length) / 2); int ca = a.Length / 2 - win / 2;
        double best = double.NegativeInfinity; int bo = 0;
        for (int off = -maxOff; off <= maxOff; off++) { int cb = ca + off; if (cb < 0 || cb + win >= b.Length) continue; double dot = 0; for (int i = 0; i < win; i += 8) dot += (double)a[ca + i] * b[cb + i]; if (dot > best) { best = dot; bo = off; } }
        return bo;
    }

    static float[] ReadMono(string path)
    {
        var bytes = File.ReadAllBytes(path); int pos = 12, dp = -1, dl = 0; short ch = 2, bits = 16;
        while (pos + 8 <= bytes.Length) { string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4); int sz = BitConverter.ToInt32(bytes, pos + 4); if (id == "fmt ") { ch = BitConverter.ToInt16(bytes, pos + 10); bits = BitConverter.ToInt16(bytes, pos + 22); } else if (id == "data") { dp = pos + 8; dl = sz; break; } pos += 8 + sz + (sz & 1); }
        int frames = dl / (2 * ch); var m = new float[frames];
        for (int i = 0; i < frames; i++) { int bp = dp + i * 2 * ch; double s = 0; for (int c = 0; c < ch; c++) s += BitConverter.ToInt16(bytes, bp + c * 2); m[i] = (float)(s / ch / 32768.0); }
        return m;
    }
}
