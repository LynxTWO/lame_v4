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
        public bool IsBool;        // rounds to 0/1; only emitted when it rounds away from Def
    }

    // Campaign 2 ranges: the ns-* clamp in parse.c is (int)(x*4) in [-32,31], i.e. +/-8 dB in
    // 0.25 dB steps -- search the full representable range (campaign 1's winner rode its +/-4
    // bound). interch was dropped: measured dead under the 3.100 psymodel. athlower is
    // content-dependent (only bites near the threshold in quiet); temporal-masking is boolean
    // (default on).
    static readonly Knob[] Knobs = {
        new Knob { Name = "ns-bass",   Lo = -8, Hi = 8, Def = 0 },
        new Knob { Name = "ns-alto",   Lo = -8, Hi = 8, Def = 0 },
        new Knob { Name = "ns-treble", Lo = -8, Hi = 8, Def = 0 },
        new Knob { Name = "ns-sfb21",  Lo = -8, Hi = 8, Def = 0 },
        new Knob { Name = "nsmsfix",   Lo = 0.5, Hi = 3.5, Def = -1 },  // -1 = absent
        new Knob { Name = "shortfactor", Lo = 0.5, Hi = 2.0, Def = 1, IsPair = true },
        new Knob { Name = "temporal-masking", Lo = 0, Hi = 1, Def = 1, IsBool = true },
        new Knob { Name = "athlower",  Lo = -6, Hi = 6, Def = 0 },
    };

    static string lamePath, nmrPath, setting;
    static string[] trainWavs;
    static int jobs;

    // VBR campaign mode (--vbr-target <kbps>): equal-setting comparison is unfair for -V
    // (a config that lowers masking simply spends more bits), so each config is scored at
    // EQUAL MEASURED BITRATE instead. Landing inside a fixed window does not work: measured
    // kbps is only piecewise continuous in fractional -V (psymodel parameters step at
    // integer V and at internal switch points; measured cliffs of 8-35 kbps, one synthetic
    // 101 kbps), and any file whose cliff spans the window kills the whole config -- the
    // first campaign-11 run evaluated 18 of 256 configs that way, all baseline-equivalent.
    // So instead: per file, bisect -V until the target is bracketed by the nearest encode
    // above and below, score BOTH endpoints, and interpolate NMR linearly to exactly the
    // target. Every file and every config gets the identical treatment.
    static double vbrTarget = 0;   // 0 = CBR/ABR mode (use `setting` as-is)

    // Measured average audio-frame bitrate: Layer III header walk (ID3v2 skipped; the
    // Xing/info frame counts, it is identical for every candidate so it cancels). Handles
    // MPEG1, MPEG2 AND MPEG2.5: LAME auto-resamples below ~32 kbps/channel, and a
    // high -V probe during bisection can emit MPEG2 frames at 22.05/24 kHz -- an
    // MPEG1-only walker misparses those files into garbage bitrates (this corrupted the
    // first bracket-mode smoke: fake >128 kbps readings on -V 9.99 encodes).
    static double AudioFrameKbps(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int pos = 0;
        if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            pos = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        int[] br1 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 };
        int[] br2 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, -1 };
        int[] sr1 = { 44100, 48000, 32000, -1 };
        long bytes = 0; double seconds = 0;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int ver = (b[pos + 1] >> 3) & 3, layer = (b[pos + 1] >> 1) & 3;
            if (ver == 1 || layer != 1) { pos++; continue; }   // reserved version / not Layer III
            int bi = (b[pos + 2] >> 4) & 15, si = (b[pos + 2] >> 2) & 3, pad = (b[pos + 2] >> 1) & 1;
            bool mpeg1 = ver == 3;
            int bitrate = mpeg1 ? br1[bi] : br2[bi];
            int rate = sr1[si];
            if (rate > 0 && ver == 2) rate /= 2;       // MPEG2
            if (rate > 0 && ver == 0) rate /= 4;       // MPEG2.5
            if (bitrate <= 0 || rate <= 0) { pos++; continue; }
            // MPEG2/2.5 Layer III frames carry 576 samples, half the MPEG1 1152
            int flen = (mpeg1 ? 144000 : 72000) * bitrate / rate + pad;
            bytes += flen; seconds += (mpeg1 ? 1152.0 : 576.0) / rate;
            pos += flen;
        }
        return seconds > 0 ? bytes * 8.0 / seconds / 1000.0 : -1;
    }

    // Campaign-4/5 fitness: minimizing mean NMR alone is exploitable -- campaign 3's winner
    // generalized on the mean while nearly doubling the loudness of the errors that stayed
    // audible on MUSIC (audNMR 5.2 -> 9.0) and trashing transient treble. A train-MEAN audNMR
    // constraint (campaign 4) never fired: the synthetic torture files' huge audNMR drowns a
    // music-only regression (Simpson's paradox inside the fitness). So the constraint is
    // PER FILE: any file whose audible-band loudness (nmr field 3) rises more than AUD_TOL
    // above ITS OWN all-defaults baseline adds a heavy penalty. Configs must win by making
    // noise less audible -- on every file -- not by making remaining audible noise louder.
    const double AUD_TOL = 0.10;
    const double AUD_LAMBDA = 10.0;
    static double[] baselineAudPerFile;

    // Campaign-6 veto: train-side constraints proved satisfiable-without-transferring
    // (campaign 5). So candidates that would become the incumbent must additionally SURVIVE
    // a validation split the fitness never optimizes: fresh library tracks (--val dir) where
    // no single file's audible-band loudness may rise >VAL_FILE_TOL over its stock baseline
    // and the mean may not rise >VAL_MEAN_TOL. The final holdouts (SQAM + h-set) still never
    // enter the loop at all.
    const double VAL_FILE_TOL = 0.50;
    const double VAL_MEAN_TOL = 0.10;
    static string[] valWavs;
    static double[] baselineValAud;
    static double baselineValAudMean = double.NaN;
    static readonly Dictionary<string, bool> vetCache = new Dictionary<string, bool>();

    static int Main(string[] args)
    {
        string train = null, valDir = null, outCsv = "autotune_results.csv";
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
                case "--val": valDir = args[++i]; break;
                case "--setting": setting = args[++i]; break;
                case "--vbr-target": vbrTarget = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
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
        if (valDir != null)
        {
            valWavs = Directory.GetFiles(valDir, "*.wav").OrderBy(f => f).ToArray();
            if (valWavs.Length == 0) { Console.Error.WriteLine("no wavs in val dir"); return 2; }
        }

        if (vbrTarget > 0)
        {
            trainWavs = FilterBracketable(trainWavs, "train");
            if (valWavs != null) valWavs = FilterBracketable(valWavs, "val");
            if (trainWavs.Length == 0) { Console.Error.WriteLine("no bracketable train files"); return 2; }
        }
        string modeDesc = vbrTarget > 0 ? $"VBR interpolated at {vbrTarget} kbps measured" : $"setting=\"{setting}\"";
        Console.WriteLine($"train={trainWavs.Length} files  val={(valWavs?.Length ?? 0)}  {modeDesc}  random={nRandom} refine={nRefine} jobs={jobs}");

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

        // Rounds 2..: coordinate refinement around the VETO-SURVIVING incumbent -- configs
        // whose audibility profile does not transfer to the validation split can never seed
        // a refinement, however good their train fitness (campaign 6).
        double[] incumbent = PickIncumbent(results, baseline, 12);
        for (int r = 0; r < nRefine; r++)
        {
            var best = incumbent;
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
            incumbent = PickIncumbent(results, incumbent, 12);
        }

        using (var w = new StreamWriter(outCsv))
        {
            w.WriteLine(string.Join(",", Knobs.Select(k => k.Name)) + ",meanNMRdb");
            foreach (var (x, s) in results.OrderBy(t => t.score))
                w.WriteLine(string.Join(",", x.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))
                            + "," + s.ToString("F4", CultureInfo.InvariantCulture));
        }
        Console.WriteLine($"\nall {results.Count} configs -> {outCsv}");
        double incScore = results.Where(t => ArgsFor(t.x) == ArgsFor(incumbent))
                                 .Select(t => t.score).DefaultIfEmpty(baseScore).Min();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WINNER (veto-surviving incumbent) {0:F4} dB (baseline {1:F4}, delta {2:+0.0000;-0.0000})\n  lame {3} {4}",
            incScore, baseScore, incScore - baseScore,
            vbrTarget > 0 ? $"-V <bisected to {vbrTarget} kbps measured>" : setting,
            ArgsFor(incumbent) == "" ? "(baseline: nothing survived the veto)" : ArgsFor(incumbent)));
        Console.WriteLine("VALIDATE on the holdout before believing this (SQAM + full tracks + transient metrics).");
        return 0;
    }

    static string ArgsFor(double[] x)
    {
        var parts = new List<string>();
        for (int d = 0; d < Knobs.Length; d++)
        {
            var k = Knobs[d];
            if (k.IsBool)
            {
                int v = x[d] >= 0.5 ? 1 : 0;
                if (v != (int) k.Def)
                    parts.Add("--" + k.Name + " " + v);
                continue;
            }
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

    // One pass over a wav set: mean meanNMRdb + per-file audibleNMRdb. False on any failure.
    static bool EvaluateOn(string[] wavs, double[] x, out double mean, out double[] audPerFile)
    {
        mean = double.NaN;
        audPerFile = new double[wavs.Length];
        string dir = Path.Combine(Path.GetTempPath(), "autotune_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            string extra = ArgsFor(x);
            double sum = 0;
            for (int i = 0; i < wavs.Length; i++)
            {
                string mp3 = Path.Combine(dir, "c.mp3"), dec = Path.Combine(dir, "c.wav");
                // No -t here: without the LAME info tag the decoder cannot strip encoder
                // delay/padding, alignment fails, and the meter reads +14 dB of pure offset
                // artifact (audFrac ~1.0). That bug silently degraded the fitness of
                // campaigns 1-6 and blinded the audNMR constraints and the val veto.
                // (-t stays correct in regress.ps1, which compares hashes, not audio.)
                if (vbrTarget > 0)
                {
                    // Bracket the target (kbps falls as V rises), keeping the nearest
                    // encode on each side, then interpolate both meter fields to exactly
                    // vbrTarget. See the --vbr-target comment for why landing-in-a-window
                    // is not viable. --resample 44.1 pins the output to MPEG1: without it,
                    // LAME auto-resamples high-V probes to 22.05/24 kHz, and for hard
                    // files the only below-target bracket endpoint is a resampled encode
                    // whose 22 kHz decode the meter scores as a +130 dB catastrophe. At
                    // the -V 3-6 range where the target actually lives the flag is a
                    // behavioral no-op (44.1 kHz is already the default for this corpus).
                    string mpA = Path.Combine(dir, "a.mp3"), mpB = Path.Combine(dir, "b.mp3");
                    double kA = double.NaN, kB = double.NaN;   // nearest above / below
                    double lo = 0.0, hi = 9.99, v = 4.0;
                    for (int it = 0; it < 14; it++)
                    {
                        string vs = v.ToString("0.###", CultureInfo.InvariantCulture);
                        if (!Run(lamePath, $"--quiet --nohist -V {vs} --resample 44.1 {extra} {Quote(wavs[i])} {Quote(mp3)}"))
                            { Console.WriteLine($"   eval fail: encode {Path.GetFileName(wavs[i])} -V {vs} {extra}"); return false; }
                        double k = AudioFrameKbps(mp3);
                        if (k <= 0)
                            { Console.WriteLine($"   eval fail: unparseable {Path.GetFileName(wavs[i])} -V {vs} {extra}"); return false; }
                        if (k > vbrTarget) { if (double.IsNaN(kA) || k < kA) { kA = k; File.Copy(mp3, mpA, true); } lo = v; }
                        else               { if (double.IsNaN(kB) || k > kB) { kB = k; File.Copy(mp3, mpB, true); } hi = v; }
                        if (!double.IsNaN(kA) && !double.IsNaN(kB) && kA - kB < 0.25) break;
                        if (hi - lo < 0.004) break;
                        v = (lo + hi) / 2;
                    }
                    if (double.IsNaN(kA) && double.IsNaN(kB))
                        { Console.WriteLine($"   eval fail: no encode at all for {Path.GetFileName(wavs[i])} {extra}"); return false; }
                    double mi, ai;
                    if (double.IsNaN(kA))
                    {
                        // Ceiling below target: even -V0 cannot spend the full budget on
                        // this file under this config (knobs can cut demand >20%, past any
                        // pre-filter margin). Score the config's best encode WITHIN the
                        // budget -- honest "best it can do at <= target size", no NaN.
                        if (!Score(wavs[i], mpB, dec, out mi, out ai)) return false;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "   note: {0} ceiling {1:F1} < {2}; scored at ceiling | {3}",
                            Path.GetFileName(wavs[i]), kB, vbrTarget, extra));
                    }
                    else if (double.IsNaN(kB))
                    {
                        // Floor above target: the config overspends on this file at any -V.
                        // Scored at the floor (more bits than budget, mild advantage); the
                        // strict equal-size holdout validation catches any winner built on it.
                        if (!Score(wavs[i], mpA, dec, out mi, out ai)) return false;
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "   note: {0} floor {1:F1} > {2}; scored at floor | {3}",
                            Path.GetFileName(wavs[i]), kA, vbrTarget, extra));
                    }
                    else
                    {
                        if (!Score(wavs[i], mpA, dec, out double mA, out double aA)) return false;
                        if (!Score(wavs[i], mpB, dec, out double mB, out double aB)) return false;
                        double t = (vbrTarget - kB) / (kA - kB);
                        mi = mB + (mA - mB) * t;
                        ai = aB + (aA - aB) * t;
                    }
                    sum += mi;
                    audPerFile[i] = ai;
                    continue;
                }
                if (!Run(lamePath, $"--quiet --nohist {setting} {extra} {Quote(wavs[i])} {Quote(mp3)}"))
                    return false;
                if (!Score(wavs[i], mp3, dec, out double m, out double a))
                    return false;
                sum += m;
                audPerFile[i] = a;
            }
            mean = sum / wavs.Length;
            return true;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    // Decode one mp3 and run the meter: field 1 = meanNMRdb, field 3 = audNMRdb.
    static bool Score(string wav, string mp3, string dec, out double meanNmr, out double audNmr)
    {
        meanNmr = audNmr = double.NaN;
        if (!Run(lamePath, $"--quiet --decode {Quote(mp3)} {Quote(dec)}"))
            return false;
        string line = RunCapture(nmrPath, $"{Quote(wav)} {Quote(dec)}");
        var f = (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 3) return false;
        meanNmr = double.Parse(f[0], CultureInfo.InvariantCulture);
        audNmr = double.Parse(f[2], CultureInfo.InvariantCulture);
        return true;
    }

    // Train fitness (campaign-5 form: mean + per-file audNMR penalty); NaN on failure.
    // The very first call (all-defaults) records the train AND val baselines.
    static double Evaluate(double[] x)
    {
        if (!EvaluateOn(trainWavs, x, out double mean, out double[] aud))
            return double.NaN;
        if (baselineAudPerFile == null)
        {
            baselineAudPerFile = aud;
            if (valWavs != null && valWavs.Length > 0
                && EvaluateOn(valWavs, x, out _, out double[] vaud))
            {
                baselineValAud = vaud;
                baselineValAudMean = vaud.Average();
            }
            return mean;
        }
        double penalty = 0;
        for (int i = 0; i < aud.Length; i++)
            penalty += Math.Max(0.0, aud[i] - (baselineAudPerFile[i] + AUD_TOL));
        return mean + AUD_LAMBDA * penalty / trainWavs.Length;
    }

    // Veto on the validation split (see VAL_* note). Passing means "the audibility profile
    // transfers"; failing configs can never become the incumbent, however good their fitness.
    static bool Vet(double[] x)
    {
        if (valWavs == null || valWavs.Length == 0 || baselineValAud == null)
            return true;    // no val set configured: veto disabled
        string key = ArgsFor(x);
        if (vetCache.TryGetValue(key, out bool cached))
            return cached;
        bool pass = false;
        double vMean = double.NaN;
        int worst = -1;
        double worstDelta = double.NegativeInfinity;
        if (EvaluateOn(valWavs, x, out _, out double[] vaud))
        {
            vMean = vaud.Average();
            pass = vMean <= baselineValAudMean + VAL_MEAN_TOL;
            for (int i = 0; i < vaud.Length; i++)
            {
                double d = vaud[i] - baselineValAud[i];
                if (d > worstDelta) { worstDelta = d; worst = i; }
                if (d > VAL_FILE_TOL)
                    pass = false;
            }
        }
        vetCache[key] = pass;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "   vet {0}: valAud {1:F3} (base {2:F3}), worst file +{3:F3} [{4}]: {5}",
            pass ? "PASS" : "reject", vMean, baselineValAudMean, worstDelta, worst,
            key == "" ? "(baseline)" : key));
        return pass;
    }

    // Keep only files whose stock -V range brackets the target WITH HEADROOM. Two encodes
    // per file: the extremes. Files that top out below the target at any -V (near-silent
    // synthetics) can never be scored at the target and would kill every config. Files
    // whose ceiling is only just above the target are landmines: campaign knobs shift
    // measured bitrate by up to ~10-20%, so a config that trims a near-ceiling file's
    // demand below the target NaNs the whole config (plucked_harmonics, stock ceiling
    // ~130, killed 3 of 6 smoke configs). Hence the 1.2x / 0.85x margins.
    static string[] FilterBracketable(string[] wavs, string label)
    {
        var keep = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), "autotune_land_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var wav in wavs)
            {
                string mp3 = Path.Combine(dir, "c.mp3");
                double kHi = -1, kLo = -1;
                if (Run(lamePath, $"--quiet --nohist -V 0 --resample 44.1 {Quote(wav)} {Quote(mp3)}"))
                    kHi = AudioFrameKbps(mp3);
                if (Run(lamePath, $"--quiet --nohist -V 9.99 --resample 44.1 {Quote(wav)} {Quote(mp3)}"))
                    kLo = AudioFrameKbps(mp3);
                if (kHi > vbrTarget * 1.2 && kLo > 0 && kLo <= vbrTarget * 0.85) keep.Add(wav);
                else Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "   {0}: {1} cannot bracket {2} kbps with margin (stock range {3:F1}..{4:F1}, need <={5:F0} and >={6:F0}), dropped",
                    label, Path.GetFileName(wav), vbrTarget, kLo, kHi, vbrTarget * 0.85, vbrTarget * 1.2));
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
        return keep.ToArray();
    }

    // Best-by-fitness config that survives the veto; performs at most maxVets fresh vets.
    static double[] PickIncumbent(List<(double[] x, double score)> results, double[] fallback, int maxVets)
    {
        int fresh = 0;
        foreach (var (x, _) in results.OrderBy(t => t.score))
        {
            bool isFresh = !vetCache.ContainsKey(ArgsFor(x));
            if (isFresh && fresh >= maxVets)
                break;
            if (isFresh) fresh++;
            if (Vet(x))
                return x;
        }
        return fallback;
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
