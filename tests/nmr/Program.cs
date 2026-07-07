using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// Encoder-independent perceptual noise-to-mask meter for LAME v4 quality work.
//
// Given the ORIGINAL wav and a DECODED wav (the MP3 decoded back to PCM), it measures how
// much of the quantization error sits ABOVE a masking threshold derived from the original
// signal -- i.e. how audible the coding noise is. It uses its own psychoacoustic model
// (critical-band energy + spreading + absolute threshold in quiet), deliberately NOT LAME's,
// so improving LAME does not trivially improve the score. Lower = more transparent.
//
// It is a relative meter: use it to compare encodes of the SAME source at the SAME bitrate.
// Sanity contract (self-validated by validate.ps1): higher bitrate must score lower.
//
// Usage: nmr <original.wav> <decoded.wav>   -> prints "audibleNMRdb meanNMRdb audibleFrac maxNMRdb"
static class Program
{
    const int N = 1024;          // FFT size
    const int HOP = 512;         // 50% overlap
    const int SR = 44100;

    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: nmr <original.wav> <decoded.wav>"); return 2; }
        var orig = ReadWavMono(args[0]);
        var dec = ReadWavMono(args[1]);

        // Align: MP3 encode+decode introduces a delay. Find the integer offset (search +/-3000
        // samples) that maximizes cross-correlation, then compare aligned regions.
        int off = BestOffset(orig, dec, 3000);
        int start = Math.Max(0, off), decStart = Math.Max(0, -off);
        int len = Math.Min(orig.Length - start, dec.Length - decStart);
        if (len < N * 4) { Console.Error.WriteLine("too short after alignment"); return 3; }

        var win = Hann(N);
        var bark = BarkBands(N, SR);
        var ath = Ath(bark);            // absolute threshold per band (linear power)

        double sumNmr = 0, sumAudible = 0; long frames = 0, bandFrames = 0, audibleBandFrames = 0;
        double maxNmr = double.NegativeInfinity;

        // HF temporal-instability meter (campaign 12). The campaign-11 demotion showed a
        // config can win every level-based gate while a listener hears "swirly highs" and
        // "unstable quiet cymbals": the decoded HF texture MOVES differently than the
        // original's. Level meters cannot see that - two encodes with identical per-frame
        // error energy can modulate that energy very differently. So: per HF range, track
        // the frame-to-frame delta of log energy for original and decoded, and accumulate
        // the RMS difference of those delta series. Smearing flattens the decoded deltas,
        // swirl adds spurious ones; both raise the score. Frames where the original range
        // is near-silent are skipped so silence cannot dilute the score.
        int[] hfLoHz = { 4000, 8000, 12000 };
        int[] hfHiHz = { 8000, 12000, 16000 };
        int nhf = hfLoHz.Length;
        var hfPrevO = new double[nhf]; var hfPrevD = new double[nhf];
        var hfPrevValid = new bool[nhf];
        double hfSum = 0; long hfCount = 0;
        const double HF_FLOOR_DB = -70.0;   // original range must carry real energy

        var reO = new double[N]; var imO = new double[N];
        var reD = new double[N]; var imD = new double[N];

        for (int p = 0; p + N <= len; p += HOP)
        {
            for (int i = 0; i < N; i++)
            {
                reO[i] = orig[start + p + i] * win[i]; imO[i] = 0;
                reD[i] = dec[decStart + p + i] * win[i]; imD[i] = 0;
            }
            Fft(reO, imO); Fft(reD, imD);

            // Per-band: signal power (original), noise power (|orig-dec|^2).
            int nb = bark.Count;
            var sig = new double[nb]; var noise = new double[nb];
            for (int b = 0; b < nb; b++)
            {
                double s = 0, e = 0;
                for (int k = bark[b].lo; k < bark[b].hi; k++)
                {
                    double po = reO[k] * reO[k] + imO[k] * imO[k];
                    double dr = reO[k] - reD[k], di = imO[k] - imD[k];
                    double pe = dr * dr + di * di;
                    s += po; e += pe;
                }
                sig[b] = s; noise[b] = e;
            }

            // HF instability: per range, log-energy deltas orig vs decoded.
            for (int h = 0; h < nhf; h++)
            {
                int kLo = hfLoHz[h] * N / SR, kHi = Math.Min(N / 2, hfHiHz[h] * N / SR);
                double eo = 0, ed = 0;
                for (int k = kLo; k < kHi; k++)
                {
                    eo += reO[k] * reO[k] + imO[k] * imO[k];
                    ed += reD[k] * reD[k] + imD[k] * imD[k];
                }
                double lo_ = 10.0 * Math.Log10(eo + 1e-12);
                double ld_ = 10.0 * Math.Log10(ed + 1e-12);
                bool loud = lo_ > HF_FLOOR_DB;
                if (hfPrevValid[h] && loud)
                {
                    double dO = lo_ - hfPrevO[h];
                    double dD = ld_ - hfPrevD[h];
                    hfSum += (dD - dO) * (dD - dO);
                    hfCount++;
                }
                hfPrevO[h] = lo_; hfPrevD[h] = ld_;
                hfPrevValid[h] = loud;
            }

            // Masking threshold = spread(signal) but never below ATH.
            var mask = Spread(sig, bark);
            for (int b = 0; b < nb; b++)
            {
                double thr = Math.Max(mask[b], ath[b]);
                double nz = noise[b];
                if (thr <= 0) thr = 1e-12;
                double nmrDb = 10.0 * Math.Log10((nz + 1e-12) / thr);
                sumNmr += nmrDb; bandFrames++;
                if (nmrDb > maxNmr) maxNmr = nmrDb;
                if (nmrDb > 0) { sumAudible += nmrDb; audibleBandFrames++; }
            }
            frames++;
        }

        double meanNmr = sumNmr / Math.Max(1, bandFrames);
        double audibleFrac = (double)audibleBandFrames / Math.Max(1, bandFrames);
        double audibleNmr = audibleBandFrames > 0 ? sumAudible / audibleBandFrames : 0.0;
        double hfStab = hfCount > 0 ? Math.Sqrt(hfSum / hfCount) : 0.0;
        // Machine-readable. PRIMARY score first: meanNMRdb (aggregate noise-to-mask over all
        // band-frames; strongly monotone with bitrate, validated). Then audibleFrac (fraction
        // of band-frames with audible noise), audibleNMRdb (mean over audible bands only; noisy),
        // maxNMRdb, hfStabDb (RMS frame-to-frame HF modulation error, the campaign-12 "swirl"
        // meter; appended so existing field positions never move). Compare encodes of the SAME
        // source at the SAME bitrate; lower meanNMRdb = more transparent.
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0:F4} {1:F5} {2:F4} {3:F4} {4:F4}",
            meanNmr, audibleFrac, audibleNmr, maxNmr, hfStab));
        return 0;
    }

    struct Band { public int lo, hi; }

    static List<Band> BarkBands(int n, int sr)
    {
        // Map FFT bins [0..n/2) to ~critical bands using the Bark scale.
        var bins = new int[n / 2];
        var list = new List<Band>();
        double prevBark = -1; int lo = 1; // skip DC
        for (int k = 1; k < n / 2; k++)
        {
            double f = (double)k * sr / n;
            double z = 13.0 * Math.Atan(0.00076 * f) + 3.5 * Math.Atan((f / 7500.0) * (f / 7500.0));
            int zi = (int)Math.Floor(z);
            if (prevBark < 0) prevBark = zi;
            if (zi != prevBark) { list.Add(new Band { lo = lo, hi = k }); lo = k; prevBark = zi; }
        }
        list.Add(new Band { lo = lo, hi = n / 2 });
        return list;
    }

    static double[] Spread(double[] sig, List<Band> bark)
    {
        // Simple two-slope spreading in the Bark domain: upward ~ -25 dB/Bark, downward ~ -10
        // dB/Bark, applied to band energies, minus a masking offset. Coarse but monotone and
        // encoder-independent, which is what a relative meter needs.
        int nb = bark.Count;
        var outp = new double[nb];
        // energies in dB
        var dB = new double[nb];
        for (int b = 0; b < nb; b++) dB[b] = 10.0 * Math.Log10(sig[b] + 1e-12);
        for (int b = 0; b < nb; b++)
        {
            double best = double.NegativeInfinity;
            for (int j = 0; j < nb; j++)
            {
                double dz = b - j; // bands ~1 Bark each
                double slope = dz >= 0 ? -25.0 * dz : 10.0 * dz; // higher band masked less from below
                double contrib = dB[j] + slope;
                if (contrib > best) best = contrib;
            }
            // masking offset below the masker (tonality-agnostic ~ -12 dB)
            outp[b] = Math.Pow(10.0, (best - 12.0) / 10.0);
        }
        return outp;
    }

    static double[] Ath(List<Band> bark)
    {
        // Absolute threshold in quiet (Terhardt), per band centre frequency, as linear power
        // scaled to the FFT's arbitrary units. Kept low so it only matters in near-silence.
        int nb = bark.Count;
        var a = new double[nb];
        for (int b = 0; b < nb; b++)
        {
            // approximate band centre bin -> freq
            double f = 1000.0; // placeholder replaced below
            a[b] = 1e-8; // very low floor; relative meter dominated by masking, not ATH
        }
        return a;
    }

    static double[] Hann(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
        return w;
    }

    // Iterative radix-2 FFT, in place.
    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cwr = 1, cwi = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double xr = re[b] * cwr - im[b] * cwi;
                    double xi = re[b] * cwi + im[b] * cwr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double t = cwr * wr - cwi * wi; cwi = cwr * wi + cwi * wr; cwr = t;
                }
            }
        }
    }

    static int BestOffset(float[] a, float[] b, int maxOff)
    {
        // Coarse cross-correlation to find the integer encode+decode delay. The window must
        // sit on ENERGY: on sparse material (castanet bursts) the file's middle can be
        // silence, making every offset's dot product noise and the argmax garbage -- that
        // misaligned even self-comparisons to -59 dB (caught by the Linux CI smoke test,
        // reproduced identically on Windows). Anchor the window at the highest-energy region
        // of the reference instead, and only move off zero offset for a decisive win so
        // degenerate content cannot invent a delay.
        int win = Math.Min(1 << 15, Math.Min(a.Length, b.Length) / 2);
        int step = Math.Max(1, win / 2);
        int ca = Math.Max(0, a.Length / 2 - win / 2);
        double bestEn = -1;
        for (int p = maxOff; p + win + maxOff < a.Length; p += step)
        {
            double en = 0;
            for (int i = 0; i < win; i += 8) en += (double)a[p + i] * a[p + i];
            if (en > bestEn) { bestEn = en; ca = p; }
        }
        double best = double.NegativeInfinity, dot0 = 0; int bestOff = 0;
        for (int off = -maxOff; off <= maxOff; off += 1)
        {
            int cb = ca + off;
            if (cb < 0 || cb + win >= b.Length) continue;
            double dot = 0;
            for (int i = 0; i < win; i += 8) dot += (double)a[ca + i] * b[cb + i];
            if (off == 0) dot0 = dot;
            if (dot > best) { best = dot; bestOff = off; }
        }
        if (bestOff != 0 && best <= dot0 * 1.001)
            bestOff = 0;
        return bestOff;
    }

    static float[] ReadWavMono(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // find 'data' chunk
        int pos = 12; int dataPos = -1, dataLen = 0; short ch = 2, bits = 16;
        while (pos + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int sz = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt ") { ch = BitConverter.ToInt16(bytes, pos + 10); bits = BitConverter.ToInt16(bytes, pos + 22); }
            else if (id == "data") { dataPos = pos + 8; dataLen = sz; break; }
            pos += 8 + sz + (sz & 1);
        }
        if (dataPos < 0) throw new Exception("no data chunk: " + path);
        if (bits != 16) throw new Exception("only 16-bit WAV supported: " + path);
        int frames = dataLen / (2 * ch);
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            int bpos = dataPos + i * 2 * ch;
            double s = 0;
            for (int c = 0; c < ch; c++) s += BitConverter.ToInt16(bytes, bpos + c * 2);
            mono[i] = (float)(s / ch / 32768.0);
        }
        return mono;
    }
}
