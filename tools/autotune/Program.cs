using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Meter-driven END-TO-END auto-tuning of LAME's hand-tuned psymodel constants (the
// "relocated frontier" after FINDINGS Finding 5: per-granule candidate selection is
// structurally leaky, but changing a GLOBAL parameter and scoring COMPLETE encodes with the
// encoder-independent meter has no such leak).
//
// Round 1 searches the six knobs LAME already exposes on the command line -- no encoder
// changes, so every result is reproducible with the shipped binary:
//   --ns-bass/--ns-alto/--ns-treble  masking adjust (dB) for low/mid/high scalefactor bands
//   --ns-sfb21                       extra treble adjust for the top band
//   --nsmsfix                        M/S switching tuning
//   --shortthreshold x,y             short-block (transient) switching thresholds
//
// Method: random search over bounded ranges, then coordinate refinement around the best
// point. Each config encodes the whole TRAIN corpus and is scored by mean meanNMRdb (the
// external meter); configs run in parallel (one sequential encode chain per config, N
// configs at once). The HOLDOUT corpus (SQAM + full-length tracks) must never be touched
// during search -- validate the winner against it separately to catch meter-overfitting.
//
// Usage:
//   autotune --train <dir> [--setting "-q 0 -b 128"] [--random 128] [--refine 2]
//            [--jobs N] [--lame path] [--nmr path] [--out results.csv]
static class Program
{
    sealed class Knob
    {
        public string Name;        // CLI option (without --)
        public double Lo, Hi;      // search range
        public double Def;         // LAME default = "absent from command line"
        public bool IsPair;        // shortthreshold takes "x,y"; we scale both from one factor
    }

    // Ranges are deliberately conservative: +/- a few dB of masking adjust, the documented
    // 0..3.5 for nsmsfix, and a 0.5..2x factor on the short-block thresholds (4.4 / 25).
    static readonly Knob[] Knobs = {
        new Knob { Name = "ns-bass",   Lo = -4, Hi = 4, Def = 0 },
        new Knob { Name = "ns-alto",   Lo = -4, Hi = 4, Def = 0 },
        new Knob { Name = "ns-treble", Lo = -4, Hi = 4, Def = 0 },
        new Knob { Name = "ns-sfb21",  Lo = -4, Hi = 4, Def = 0 },
        new Knob { Name = "nsmsfix",   Lo = 0.5, Hi = 3.5, Def = -1 },  // -1 = absent
        new Knob { Name = "shortfactor", Lo = 0.5, Hi = 2.0, Def = 1, IsPair = true },
    };

    static string lamePath, nmrPath, setting;
    static string[] trainWavs;
    static int jobs;

    static int Main(string[] args)
    {
        string train = null, outCsv = "autotune_results.csv";
        int nRandom = 128, nRefine = 2;
        setting = "-q 0 -b 128";
        jobs = Environment.ProcessorCount;
        string repo = FindRepoRoot(AppContext.BaseDirectory);
        lamePath = repo != null ? Path.Combine(repo, "output", "lame.exe") : "lame.exe";
        nmrPath = repo != null ? Path.Combine(repo, "tests", "nmr", "bin", "Release", "net8.0", "nmr.exe") : "nmr.exe";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--train": train = args[++i]; break;
                case "--setting": setting = args[++i]; break;
                case "--random": nRandom = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--refine": nRefine = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--lame": lamePath = args[++i]; break;
                case "--nmr": nmrPath = args[++i]; break;
                case "--out": outCsv = args[++i]; break;
                default: Console.Error.WriteLine($"unknown option {args[i]}"); return 2;
            }
        }
        if (train == null || !Directory.Exists(train))
        {
            Console.Error.WriteLine("usage: autotune --train <wav-dir> [--setting \"-q 0 -b 128\"] [--random N] [--refine R]");
            return 2;
        }
        foreach (var p in new[] { lamePath, nmrPath })
            if (!File.Exists(p)) { Console.Error.WriteLine($"not found: {p}"); return 2; }
        trainWavs = Directory.GetFiles(train, "*.wav").OrderBy(f => f).ToArray();
        if (trainWavs.Length == 0) { Console.Error.WriteLine("no wavs in train dir"); return 2; }

        Console.WriteLine($"train={trainWavs.Length} files  setting=\"{setting}\"  random={nRandom} refine={nRefine} jobs={jobs}");

        var results = new List<(double[] x, double score)>();

        // The baseline (all knobs at default = absent) anchors everything.
        double[] baseline = Knobs.Select(k => k.Def).ToArray();
        double baseScore = Evaluate(baseline);
        Console.WriteLine($"BASELINE meanNMR = {baseScore:F4} dB");
        results.Add((baseline, baseScore));

        // Round 1: random search, deterministic seed for reproducibility.
        var rng = new Random(42);
        var configs = new List<double[]>();
        for (int i = 0; i < nRandom; i++)
            configs.Add(Knobs.Select(k => k.Lo + rng.NextDouble() * (k.Hi - k.Lo)).ToArray());
        EvaluateBatch(configs, results);
        Report(results, baseScore, "after random search");

        // Rounds 2..: coordinate refinement around the incumbent best.
        for (int r = 0; r < nRefine; r++)
        {
            var best = results.OrderBy(t => t.score).First().x;
            configs = new List<double[]>();
            for (int d = 0; d < Knobs.Length; d++)
            {
                double span = (Knobs[d].Hi - Knobs[d].Lo) / (4 << r); // shrinking neighborhood
                foreach (var delta in new[] { -span, -span / 2, span / 2, span })
                {
                    var x = (double[]) best.Clone();
                    x[d] = Math.Clamp(x[d] + delta, Knobs[d].Lo, Knobs[d].Hi);
                    configs.Add(x);
                }
            }
            EvaluateBatch(configs, results);
            Report(results, baseScore, $"after refinement {r + 1}");
        }

        using (var w = new StreamWriter(outCsv))
        {
            w.WriteLine(string.Join(",", Knobs.Select(k => k.Name)) + ",meanNMRdb");
            foreach (var (x, s) in results.OrderBy(t => t.score))
                w.WriteLine(string.Join(",", x.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))
                            + "," + s.ToString("F4", CultureInfo.InvariantCulture));
        }
        Console.WriteLine($"\nall {results.Count} configs -> {outCsv}");
        var top = results.OrderBy(t => t.score).First();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WINNER {0:F4} dB (baseline {1:F4}, delta {2:+0.0000;-0.0000})\n  lame {3} {4}",
            top.score, baseScore, top.score - baseScore, setting, ArgsFor(top.x)));
        Console.WriteLine("VALIDATE on the holdout before believing this (SQAM + full tracks + transient metrics).");
        return 0;
    }

    static string ArgsFor(double[] x)
    {
        var parts = new List<string>();
        for (int d = 0; d < Knobs.Length; d++)
        {
            var k = Knobs[d];
            if (Math.Abs(x[d] - k.Def) < 1e-9) continue;   // default = leave absent
            if (k.IsPair)
            {
                double t1 = 4.4 * x[d], t2 = 25.0 * x[d];  // scale both stock thresholds
                parts.Add("--shortthreshold " + t1.ToString("F2", CultureInfo.InvariantCulture)
                          + "," + t2.ToString("F2", CultureInfo.InvariantCulture));
            }
            else
                parts.Add("--" + k.Name + " " + x[d].ToString("F2", CultureInfo.InvariantCulture));
        }
        return string.Join(" ", parts);
    }

    static void EvaluateBatch(List<double[]> configs, List<(double[] x, double score)> results)
    {
        var scores = new double[configs.Count];
        Parallel.For(0, configs.Count, new ParallelOptions { MaxDegreeOfParallelism = jobs },
                     i => scores[i] = Evaluate(configs[i]));
        for (int i = 0; i < configs.Count; i++)
            if (!double.IsNaN(scores[i]))
                results.Add((configs[i], scores[i]));
    }

    // Mean meanNMRdb over the train corpus for one config; NaN on any failure.
    static double Evaluate(double[] x)
    {
        string dir = Path.Combine(Path.GetTempPath(), "autotune_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string extra = ArgsFor(x);
            double sum = 0;
            foreach (var wav in trainWavs)
            {
                string mp3 = Path.Combine(dir, "c.mp3"), dec = Path.Combine(dir, "c.wav");
                if (!Run(lamePath, $"--quiet --nohist -t {setting} {extra} {Quote(wav)} {Quote(mp3)}"))
                    return double.NaN;
                if (!Run(lamePath, $"--quiet --decode {Quote(mp3)} {Quote(dec)}"))
                    return double.NaN;
                string line = RunCapture(nmrPath, $"{Quote(wav)} {Quote(dec)}");
                var f = (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 1) return double.NaN;
                sum += double.Parse(f[0], CultureInfo.InvariantCulture);
            }
            return sum / trainWavs.Length;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    static void Report(List<(double[] x, double score)> results, double baseScore, string phase)
    {
        var top = results.OrderBy(t => t.score).Take(3).ToList();
        Console.WriteLine($"-- {phase}: best {top[0].score:F4} (delta {top[0].score - baseScore:+0.0000;-0.0000}) | " +
                          string.Join(" | ", top.Select(t => ArgsFor(t.x) == "" ? "(baseline)" : ArgsFor(t.x))));
    }

    static bool Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    static string RunCapture(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        string outp = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? outp.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) : null;
    }

    static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    static string FindRepoRoot(string start)
    {
        var d = new DirectoryInfo(start);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "build.cmd")) &&
                Directory.Exists(Path.Combine(d.FullName, "libmp3lame"))) return d.FullName;
            d = d.Parent;
        }
        return null;
    }
}
