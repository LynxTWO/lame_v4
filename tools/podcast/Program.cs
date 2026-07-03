using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Podcast constrained-optimizer (see docs/podcast-optimizer-design.md).
//
// Squeezes the best-sounding LEGAL MP3 out of a hard average-bitrate budget. Every candidate
// "flavor" (mode + effort + filter combination, over ABR / CBR / true-VBR foundations) is
// LANDED in the window [target - tolerance, target] by an adaptive search on its rate control
// -- fractional --abr requests (v4 float-ABR) for ABR flavors, fractional -V for VBR flavors,
// exact by construction for CBR -- then decoded and scored with the repo's encoder-independent
// perceptual meter (tests/nmr, meanNMRdb, lower = better). The winner is the best score among
// flavors that landed. So ABR, CBR and VBR each get a fair, in-window shot; the measured
// bitrate, never the requested one, decides legality.
//
// Hard rules from the design brief: no freeformat, no -B frame capping (robs the reservoir),
// candidates parallelized at whole-file level only (MP3 frames share reservoir + psy state).
static class Program
{
    enum Kind { Abr, Cbr, Vbr }

    sealed class Flavor
    {
        public string Id;
        public Kind Kind;
        public string Extra;           // args besides the rate control (mode, effort, filters)
        public int Filters;            // tie-break: fewer filter args = simpler = safer
        public double Request = -1;    // landed --abr kbps or -V value
        public double MeasuredKbps = -1;
        public bool Landed, DecodeOk;
        public double MeanNmr = double.NaN, AudFrac = double.NaN;
        public string Fail = "";
        public string FinalArgs = "";
    }

    static string lamePath, nmrPath;
    static double target, tolerance, floorKbps;
    static string scope;

    static int Main(string[] rawArgs)
    {
        // Utility mode: print an MP3's measured average audio-frame bitrate and exit.
        // Reuses the frame walker; the equal-size A/B harness (tests/eqsize-abtest.ps1)
        // depends on it to land candidates at matched measured bitrates.
        if (rawArgs.Length == 2 && rawArgs[0] == "--measure")
        {
            double k = AudioFrameKbps(rawArgs[1]);
            Console.WriteLine(k.ToString("F3", CultureInfo.InvariantCulture));
            return k > 0 ? 0 : 1;
        }

        var args = new List<string>(rawArgs);
        string input = null, output = null;
        string preset = "podcast-stereo";
        target = -1; tolerance = 0.5; scope = "audio";
        int jobs = Environment.ProcessorCount;
        bool strict = false, report = true;
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        lamePath = repoRoot != null ? Path.Combine(repoRoot, "output", "lame.exe") : "lame.exe";
        nmrPath = repoRoot != null ? Path.Combine(repoRoot, "tests", "nmr", "bin", "Release", "net8.0", "nmr.exe") : "nmr.exe";

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--preset": preset = args[++i]; break;
                case "--target": target = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--tolerance": tolerance = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--scope": scope = args[++i]; break;
                case "--jobs": jobs = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--strict": strict = true; break;
                case "--no-report": report = false; break;
                case "--lame": lamePath = args[++i]; break;
                case "--nmr": nmrPath = args[++i]; break;
                case "--out": output = args[++i]; break;
                default:
                    if (a.StartsWith("--")) { Console.Error.WriteLine($"unknown option {a}"); return 2; }
                    if (input == null) input = a; else output = a;
                    break;
            }
        }
        if (input == null)
        {
            Console.Error.WriteLine("usage: podcast <input.wav> [out.mp3] [--preset podcast-stereo|podcast-mono|podcast-stereo-efficient|podcast-mono-efficient]");
            Console.Error.WriteLine("               [--target kbps] [--tolerance 0.5] [--scope audio|file] [--jobs N] [--strict] [--lame path] [--nmr path]");
            return 2;
        }
        foreach (var p in new[] { input, lamePath, nmrPath })
            if (!File.Exists(p)) { Console.Error.WriteLine($"not found: {p}"); return 2; }

        bool mono = preset.Contains("mono");
        bool efficient = preset.Contains("efficient");
        if (target < 0) target = mono ? 96 : 192;
        floorKbps = efficient ? target * 0.75 : target - tolerance;
        if (output == null) output = Path.ChangeExtension(input, null) + $".{preset}.mp3";

        var flavors = BuildFlavors(mono, (int)Math.Round(target));
        string work = Path.Combine(Path.GetTempPath(), "podcast_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);

        Console.WriteLine($"preset={preset} target={target} kbps tol={tolerance} scope={scope} flavors={flavors.Count} jobs={jobs}");
        Console.WriteLine($"lame={lamePath}");

        // Each flavor lands itself (a short sequential bisection of encodes), then decodes and
        // scores. Flavors run in parallel, so wall time ~ the slowest single bisection.
        Parallel.ForEach(flavors, new ParallelOptions { MaxDegreeOfParallelism = jobs }, f =>
        {
            string dir = Path.Combine(work, f.Id); Directory.CreateDirectory(dir);
            string mp3 = Path.Combine(dir, "c.mp3"), dec = Path.Combine(dir, "c.wav");
            try
            {
                if (!LandFlavor(f, input, mp3)) return;
                f.DecodeOk = Run(lamePath, "--quiet --decode " + Quote(mp3) + " " + Quote(dec));
                if (!f.DecodeOk) { f.Fail = "decode"; f.Landed = false; return; }
                string line = RunCapture(nmrPath, Quote(input) + " " + Quote(dec));
                var pieces = (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length < 2) { f.Fail = "nmr"; f.Landed = false; return; }
                f.MeanNmr = double.Parse(pieces[0], CultureInfo.InvariantCulture);
                f.AudFrac = double.Parse(pieces[1], CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { f.Fail = ex.GetType().Name; f.Landed = false; }
        });

        var landed = flavors.Where(f => f.Landed).ToList();
        // Winner: lowest meanNMRdb. Near-ties (within 0.05 dB) break toward the simpler, safer
        // setting: non-VBR first, then fewer filter args, then (efficient) smaller measured rate.
        Flavor win = null;
        if (landed.Count > 0)
        {
            double best = landed.Min(f => f.MeanNmr);
            win = landed.Where(f => f.MeanNmr <= best + 0.05)
                        .OrderBy(f => f.Kind == Kind.Vbr ? 1 : 0)
                        .ThenBy(f => f.Filters)
                        .ThenBy(f => efficient ? f.MeasuredKbps : 0)
                        .First();
        }

        if (report)
        {
            Console.WriteLine();
            Console.WriteLine($"{"flavor",-22} {"kbps",8} {"landed",-7} {"meanNMR",9} {"audFrac",8}  args");
            foreach (var f in flavors.OrderBy(f => f.Landed ? f.MeanNmr : double.MaxValue))
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-22} {1,8:F2} {2,-7} {3,9} {4,8}  {5}",
                    f.Id, f.MeasuredKbps, f.Landed ? "yes" : (f.Fail == "" ? "no" : f.Fail),
                    double.IsNaN(f.MeanNmr) ? "-" : f.MeanNmr.ToString("F3", CultureInfo.InvariantCulture),
                    double.IsNaN(f.AudFrac) ? "-" : f.AudFrac.ToString("F3", CultureInfo.InvariantCulture),
                    f.FinalArgs));
        }

        if (win == null)
        {
            Console.Error.WriteLine($"\nNO flavor landed in [{floorKbps:F1}, {target:F1}] kbps." +
                " For very short clips discrete frame sizes can make the window unreachable; widen --tolerance or use --scope file.");
            TryCleanup(work);
            return strict ? 1 : 3;
        }

        File.Copy(Path.Combine(work, win.Id, "c.mp3"), output, true);
        Console.WriteLine();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WINNER {0}: {1:F2} kbps, meanNMR {2:F3} dB, audFrac {3:F3}\n  lame {4}\n  -> {5}",
            win.Id, win.MeasuredKbps, win.MeanNmr, win.AudFrac, win.FinalArgs, output));
        TryCleanup(work);
        return 0;
    }

    // The flavor list (design doc, "Candidate grid"): effort x filter combinations over the
    // three rate-control foundations. Every flavor gets landed in-window before scoring, so
    // the comparison is always at (nearly) equal measured bitrate.
    static List<Flavor> BuildFlavors(bool mono, int t)
    {
        var g = new List<Flavor>();
        string mode = mono ? "-m m" : "-m j";
        string[] lps = mono ? new[] { "12", "14", "15.5", "16" } : new[] { "16", "17", "18", "19" };
        string[] hps = mono ? new[] { "50", "70", "90" } : new[] { "40", "60", "80" };
        void Add(string id, Kind k, string extra, int filters = 0) =>
            g.Add(new Flavor { Id = id, Kind = k, Extra = extra, Filters = filters });

        // Effort sweep, plain. q0 carries the Finding 1 fix in this repo's build; --quality-max
        // is the maximum-effort mode; -q4 stays as the stock-safe baseline so the tool also
        // behaves sanely when pointed (--lame) at an unfixed build.
        Add("abr-q4", Kind.Abr, $"{mode} -q 4");
        Add("abr-q0", Kind.Abr, $"{mode} -q 0");
        Add("abr-qmax", Kind.Abr, $"{mode} --quality-max");

        // Filters at max effort: trading inaudible top octave (lowpass) or sub-voice rumble
        // (highpass) for cleaner mids is often the best deal at a fixed budget -- but only
        // measurement may decide, per input.
        foreach (var lp in lps) Add($"abr-lp{lp}", Kind.Abr, $"{mode} --quality-max --lowpass {lp}", 1);
        foreach (var hp in hps) Add($"abr-hp{hp}", Kind.Abr, $"{mode} --quality-max --highpass {hp}", 1);

        // CBR: exact-by-construction landing (padding holds the nominal rate), whole budget,
        // reservoir still active. ABR usually wins at equal average; sometimes it doesn't.
        Add("cbr-q4", Kind.Cbr, $"{mode} -q 4");
        Add("cbr-qmax", Kind.Cbr, $"{mode} --quality-max");
        foreach (var lp in lps) Add($"cbr-lp{lp}", Kind.Cbr, $"{mode} --quality-max --lowpass {lp}", 1);

        // True VBR, landed via fractional -V: better bit placement than ABR when a quality
        // level happens to average into the window -- now found by search instead of luck.
        Add("vbr", Kind.Vbr, $"{mode}");
        Add("vbr-qmax", Kind.Vbr, $"{mode} --quality-max");

        return g;
    }

    // Adaptive landing. ABR: measured average rises monotonically with the (fractional, v4)
    // --abr request; expand a bracket around the target then bisect to 0.01 kbps of request.
    // VBR: measured average falls monotonically as -V rises; bisect V in [0,9.99]. Either way
    // stop as soon as a measurement lands in [floor, target]; give up when the bracket is
    // exhausted (window genuinely unreachable, e.g. very short clips or a VBR curve that
    // jumps across the window between adjacent quantized settings).
    static bool LandFlavor(Flavor f, string input, string mp3)
    {
        double Measure(string rateArgs)
        {
            string a = rateArgs + " " + f.Extra;
            if (!Run(lamePath, "--quiet " + a + " " + Quote(input) + " " + Quote(mp3))) return -1;
            double k = scope == "file" ? FileScopeKbps(mp3) : AudioFrameKbps(mp3);
            f.MeasuredKbps = k; f.FinalArgs = a;
            return k;
        }
        bool InWindow(double k) => k >= floorKbps && k <= target;
        string Abr(double r) => "--abr " + r.ToString("0.##", CultureInfo.InvariantCulture);
        string Vbr(double v) => "-V " + v.ToString("0.##", CultureInfo.InvariantCulture);

        if (f.Kind == Kind.Cbr)
        {
            // Nearest legal CBR rate: MPEG1 L3 table. Exact landing by construction.
            int[] cbrs = { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
            int rate = cbrs.Where(r => r <= target).DefaultIfEmpty(-1).Max();
            if (rate < 0 || !InWindow(rate)) { f.Fail = "no CBR rate in window"; return false; }
            double k = Measure("-b " + rate);
            if (k < 0) { f.Fail = "encode"; return false; }
            f.Request = rate; f.Landed = InWindow(k);
            if (!f.Landed) f.Fail = "cbr off-window?";
            return f.Landed;
        }

        if (f.Kind == Kind.Abr)
        {
            double lo = -1, hi = -1;      // requests bracketing the window (lo under, hi over)
            double r = target;
            for (int i = 0; i < 12; i++)  // bracket expansion + bisection, ~12 encodes max
            {
                double k = Measure(Abr(r));
                if (k < 0) { f.Fail = "encode"; return false; }
                if (InWindow(k)) { f.Request = r; f.Landed = true; return true; }
                if (k < floorKbps)
                {
                    lo = r;
                    // Under: push the request up by the shortfall (+ margin) until bracketed.
                    r = hi < 0 ? Math.Min(320, r + (floorKbps - k) + 1.0) : (lo + hi) / 2;
                    if (lo >= 320) break;
                }
                else
                {
                    hi = r;
                    r = lo < 0 ? Math.Max(8, r - (k - target) - 1.0) : (lo + hi) / 2;
                    if (hi <= 8) break;
                }
                if (lo > 0 && hi > 0 && hi - lo < 0.01) break;
            }
            f.Fail = "window unreachable";
            return false;
        }

        // VBR: V in [0, 9.99], measured kbps decreasing in V.
        {
            double lo = 0, hi = 9.99;     // V bracket: measured(lo) >= window >= measured(hi)
            double v = mono_guess(f) ;
            for (int i = 0; i < 12; i++)
            {
                double k = Measure(Vbr(v));
                if (k < 0) { f.Fail = "encode"; return false; }
                if (InWindow(k)) { f.Request = v; f.Landed = true; return true; }
                if (k > target) lo = v; else hi = v;
                if (hi - lo < 0.01) break;
                v = (lo + hi) / 2;
            }
            f.Fail = "window unreachable";
            return false;
        }

        // Local first-guess for the VBR bisection start: mid-quality; converges either way.
        static double mono_guess(Flavor f) => f.Extra.Contains("-m m") ? 4.0 : 2.0;
    }

    // Average bitrate over the audio frames themselves: walk MPEG frame headers, sum bytes and
    // frame durations. Includes the Xing/LAME info frame (a real stream frame, identical for
    // every candidate, so it cancels in comparison). ID3v2 is skipped; trailing tags never
    // match a sync word, so the walk ends cleanly.
    static double AudioFrameKbps(string path)
    {
        long bytes = 0; double seconds = 0;
        WalkFrames(path, (flen, dur) => { bytes += flen; seconds += dur; });
        return seconds > 0 ? bytes * 8.0 / seconds / 1000.0 : -1;
    }

    static double FileScopeKbps(string path)
    {
        // Whole file (tags included) over audio duration -- what a hosting bill sees.
        double seconds = 0;
        WalkFrames(path, (flen, dur) => { seconds += dur; });
        if (seconds <= 0) return -1;
        return new FileInfo(path).Length * 8.0 / seconds / 1000.0;
    }

    static void WalkFrames(string path, Action<int, double> onFrame)
    {
        byte[] b = File.ReadAllBytes(path);
        int pos = 0;
        if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            pos = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        // MPEG1 Layer III tables (this tool only ever drives 32/44.1/48 kHz input).
        int[] br = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 };
        int[] sr = { 44100, 48000, 32000, -1 };
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int ver = (b[pos + 1] >> 3) & 3, layer = (b[pos + 1] >> 1) & 3;
            if (ver != 3 || layer != 1) { pos++; continue; }   // MPEG1 Layer III only
            int bi = (b[pos + 2] >> 4) & 15, si = (b[pos + 2] >> 2) & 3, pad = (b[pos + 2] >> 1) & 1;
            if (br[bi] <= 0 || sr[si] <= 0) { pos++; continue; }
            int flen = 144000 * br[bi] / sr[si] + pad;
            onFrame(flen, 1152.0 / sr[si]);
            pos += flen;
        }
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
        if (p.ExitCode != 0) return null;
        return outp.Split('\n').FirstOrDefault(l => l.Trim().Length > 0);
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

    static void TryCleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch { /* scratch in %TEMP%; best-effort */ }
    }
}
