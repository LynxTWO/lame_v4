using System;
using System.IO;

// Deterministic critical-signal corpus generator for LAME v4 quality regression.
// Emits 44.1 kHz / 16-bit stereo WAVs chosen to stress the parts of MP3 encoding where
// quality is won or lost: pre-echo/transients, tonal masking, HF handling, noise
// allocation, and stereo decisions. All seeded, so the corpus is byte-reproducible.
// Real music (user-supplied killer samples) drops into the same folder and is picked up
// by the regression harness alongside these.
static class Program
{
    const int SR = 44100;

    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "corpus";
        Directory.CreateDirectory(outDir);

        // Each item: name, seconds, generator.
        Write(outDir, "impulse_train", 6, (t, i, ch) =>
            // Dirac impulses every 0.5 s: the classic pre-echo trap.
            (i % (SR / 2) == 0) ? 0.95 : 0.0);

        Write(outDir, "castanet_bursts", 6, (t, i, ch) =>
        {
            // Sharp exponentially-decaying transient bursts (attack detection / window switch).
            double period = 0.35; double local = t % period;
            double env = Math.Exp(-local * 90.0);
            double noise = Hash((uint)i * 2654435761u + (uint)ch) * 2 - 1;
            return 0.9 * env * noise;
        });

        Write(outDir, "sparse_hf_tones", 8, (t, i, ch) =>
        {
            // A few high, sparse tones near the top of the band (tonality + masking, HF cutoff).
            double s = 0.28 * Math.Sin(2 * Math.PI * 12000 * t)
                     + 0.22 * Math.Sin(2 * Math.PI * 15500 * t)
                     + 0.18 * Math.Sin(2 * Math.PI * 18000 * t);
            return s * (ch == 0 ? 1.0 : 0.9);
        });

        Write(outDir, "log_sweep", 10, (t, i, ch) =>
        {
            // 20 Hz -> 20 kHz log sweep (frequency response / aliasing across the band).
            double f0 = 20, f1 = 20000, T = 10.0;
            double k = Math.Log(f1 / f0);
            double phase = 2 * Math.PI * f0 * T / k * (Math.Exp(k * t / T) - 1);
            return 0.7 * Math.Sin(phase);
        });

        Write(outDir, "pink_noise", 8, PinkNoiseGen());

        Write(outDir, "white_noise", 6, (t, i, ch) =>
            0.5 * (Hash((uint)i * 40503u + (uint)ch * 2246822519u) * 2 - 1));

        Write(outDir, "plucked_harmonics", 10, (t, i, ch) =>
        {
            // Repeated plucked-string-like decays with rich harmonics (stereo + transient).
            double period = 0.8; double local = t % period;
            double env = Math.Exp(-local * 4.0);
            double f = 196.0 * (ch == 0 ? 1.0 : 1.0 / 1.5); // different note per channel -> real stereo
            double s = 0;
            for (int h = 1; h <= 12; h++) s += (1.0 / h) * Math.Sin(2 * Math.PI * f * h * t);
            return 0.5 * env * s / 3.0;
        });

        Write(outDir, "hf_square", 5, (t, i, ch) =>
        {
            // Near-Nyquist square wave: brutal on the filterbank (ringing / aliasing).
            double f = 11025; // SR/4
            return 0.6 * (Math.Sin(2 * Math.PI * f * t) >= 0 ? 1.0 : -1.0);
        });

        Write(outDir, "silence_clicks", 6, (t, i, ch) =>
            // Long silence with rare full-scale clicks (reservoir / masking of isolated events).
            (i % SR == SR / 2) ? 0.98 : 0.0);

        Write(outDir, "music_mix", 20, (t, i, ch) =>
        {
            // A busy tonal+noise mix with amplitude modulation and per-channel detune, so the
            // encoder does representative broadband work (not a trivial signal).
            double detune = ch == 0 ? 1.0 : 1.003;
            double s = 0.26 * Math.Sin(2 * Math.PI * 220 * detune * t)
                     + 0.18 * Math.Sin(2 * Math.PI * 440 * detune * t)
                     + 0.14 * Math.Sin(2 * Math.PI * 660 * detune * t)
                     + 0.10 * Math.Sin(2 * Math.PI * (1760 + 60 * Math.Sin(3 * t)) * t)
                     + 0.08 * (Hash((uint)i * 22695477u + (uint)ch) * 2 - 1);
            double amp = 0.55 + 0.45 * Math.Sin(0.9 * t);
            return s * amp;
        });

        Console.WriteLine("corpus written to: " + Path.GetFullPath(outDir));
        return 0;
    }

    // Deterministic [0,1) hash-noise so runs are byte-identical across machines.
    static double Hash(uint x)
    {
        x ^= x >> 16; x *= 2246822519u; x ^= x >> 13; x *= 3266489917u; x ^= x >> 16;
        return x / 4294967296.0;
    }

    // Voss-McCartney-ish pink noise, seeded and stateful per channel.
    static Func<double, int, int, double> PinkNoiseGen()
    {
        var rows = new double[2][]; rows[0] = new double[16]; rows[1] = new double[16];
        var sums = new double[2]; var counters = new uint[2];
        return (t, i, ch) =>
        {
            counters[ch]++;
            uint c = counters[ch];
            int nRows = 16;
            for (int r = 0; r < nRows; r++)
            {
                if ((c & (1u << r)) != 0 || r == nRows - 1)
                {
                    sums[ch] -= rows[ch][r];
                    rows[ch][r] = Hash(c * 2654435761u + (uint)(r * 40503) + (uint)ch * 19349663u) * 2 - 1;
                    sums[ch] += rows[ch][r];
                    break;
                }
            }
            return 0.35 * sums[ch] / nRows * 4.0;
        };
    }

    static void Write(string dir, string name, int seconds, Func<double, int, int, double> gen)
    {
        int n = SR * seconds;
        string path = Path.Combine(dir, name + ".wav");
        using (var bw = new BinaryWriter(File.Create(path)))
        {
            int data = n * 4; // 16-bit stereo
            bw.Write(new char[] { 'R', 'I', 'F', 'F' }); bw.Write(36 + data);
            bw.Write(new char[] { 'W', 'A', 'V', 'E' }); bw.Write(new char[] { 'f', 'm', 't', ' ' });
            bw.Write(16); bw.Write((short)1); bw.Write((short)2);
            bw.Write(SR); bw.Write(SR * 4); bw.Write((short)4); bw.Write((short)16);
            bw.Write(new char[] { 'd', 'a', 't', 'a' }); bw.Write(data);
            var buf = new byte[n * 4]; int idx = 0;
            for (int i = 0; i < n; i++)
            {
                double tt = (double)i / SR;
                for (int ch = 0; ch < 2; ch++)
                {
                    double s = gen(tt, i, ch);
                    if (s > 1) s = 1; if (s < -1) s = -1;
                    int v = (int)Math.Round(s * 32767);
                    if (v > 32767) v = 32767; if (v < -32768) v = -32768;
                    buf[idx++] = (byte)v; buf[idx++] = (byte)(v >> 8);
                }
            }
            bw.Write(buf);
        }
    }
}
