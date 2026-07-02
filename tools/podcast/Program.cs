using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

// Podcast constrained-optimizer (see docs/podcast-optimizer-design.md).
//
// Squeezes the best-sounding LEGAL MP3 out of a hard average-bitrate budget by encoding many
// standards-compliant candidate settings in parallel (one whole-file lame process per candidate
// -- never frame-parallel: MP3 frames share reservoir + psy state), measuring each candidate's
// REAL average bitrate from its frame headers, rejecting any outside the window
// [target - tolerance, target], scoring survivors with the repo's encoder-independent
// perceptual meter (tests/nmr, meanNMRdb, lower = better), and keeping the winner.
//
// Hard rules from the design brief: no freeformat, no -B frame capping (robs the reservoir),
// ABR is the foundation, VBR candidates are probes accepted only if they land in the window,
// and the measured bitrate -- never the requested one -- decides legality.
static class Program
{
    sealed class Candidate
    {
        public string Id;
        public string[] Args;          // lame args between input and output
        public bool IsVbr;             // VBR probe (legality decided purely by measurement)
        public int Filters;            // count of lowpass/highpass args (tie-break: fewer = simpler)
        public double MeasuredKbps = -1;
        public bool EncodeOk, DecodeOk, Legal;
        public double MeanNmr = double.NaN, AudFrac = double.NaN;
        public string Fail = "";
    }

    static int Main(string[] rawArgs)
    {
        var args = new List<string>(rawArgs);
        string input = null, output = null;
        string preset = "podcast-stereo";
        double target = -1, tolerance = 0.5;
        string scope = "audio";
        int jobs = Environment.ProcessorCount;
        bool strict = false, report = true;
        string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        string lamePath = repoRoot != null ? Path.Combine(repoRoot, "output", "lame.exe") : "lame.exe";
        string nmrPath = repoRoot != null ? Path.Combine(repoRoot, "tests", "nmr", "bin", "Release", "net8.0", "nmr.exe") : "nmr.exe";

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
        if (output == null) output = Path.ChangeExtension(input, null) + $".{preset}.mp3";

        var cands = BuildGrid(mono, (int)Math.Round(target));
        string work = Path.Combine(Path.GetTempPath(), "podcast_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(work);

        Console.WriteLine($"preset={preset} target={target} kbps tol={tolerance} scope={scope} candidates={cands.Count} jobs={jobs}");
        Console.WriteLine($"lame={lamePath}");

        // Parallelize across candidates: each gets a private subdir, so 32 cores = ~32 encodes
        // at once with zero cross-talk. The MP3s themselves are encoded single-threaded whole
        // files, keeping the frame chain (reservoir, psy history) intact.
        Parallel.ForEach(cands, new ParallelOptions { MaxDegreeOfParallelism = jobs }, c =>
        {
            string dir = Path.Combine(work, c.Id); Directory.CreateDirectory(dir);
            string mp3 = Path.Combine(dir, "c.mp3"), dec = Path.Combine(dir, "c.wav");
            c.EncodeOk = Run(lamePath, Quote("--quiet") + " " + string.Join(" ", c.Args) + " " + Quote(input) + " " + Quote(mp3));
            if (!c.EncodeOk) { c.Fail = "encode"; return; }

            // Legality: the measured average, never the requested one.
            c.MeasuredKbps = scope == "file"
                ? FileScopeKbps(mp3)
                : AudioFrameKbps(mp3);
            if (c.MeasuredKbps < 0) { c.Fail = "parse"; return; }
            double floor = efficient ? target * 0.75 : target - tolerance;
            c.Legal = c.MeasuredKbps >= floor && c.MeasuredKbps <= target;
            if (!c.Legal) { c.Fail = c.MeasuredKbps > target ? "over" : "under"; return; }

            // Decode-and-score only survivors (decode also validates the stream end-to-end).
            c.DecodeOk = Run(lamePath, "--quiet --decode " + Quote(mp3) + " " + Quote(dec));
            if (!c.DecodeOk) { c.Fail = "decode"; c.Legal = false; return; }
            string line = RunCapture(nmrPath, Quote(input) + " " + Quote(dec));
            var f = (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2) { c.Fail = "nmr"; c.Legal = false; return; }
            c.MeanNmr = double.Parse(f[0], CultureInfo.InvariantCulture);
            c.AudFrac = double.Parse(f[1], CultureInfo.InvariantCulture);
        });

        var legal = cands.Where(c => c.Legal).ToList();
        // Winner: lowest meanNMRdb. Near-ties (within 0.05 dB) break toward the simpler, safer
        // setting: non-VBR first, then fewer filter args, then (efficient mode) smaller measured
        // bitrate -- "smaller if transparent".
        var ranked = legal
            .OrderBy(c => c.MeanNmr)
            .ToList();
        Candidate win = null;
        if (ranked.Count > 0)
        {
            double best = ranked[0].MeanNmr;
            win = ranked.Where(c => c.MeanNmr <= best + 0.05)
                        .OrderBy(c => c.IsVbr ? 1 : 0)
                        .ThenBy(c => c.Filters)
                        .ThenBy(c => efficient ? c.MeasuredKbps : 0)
                        .First();
        }

        if (report)
        {
            Console.WriteLine();
            Console.WriteLine($"{"candidate",-26} {"kbps",8} {"legal",-6} {"meanNMR",9} {"audFrac",8}  args");
            foreach (var c in cands.OrderBy(c => c.Legal ? c.MeanNmr : double.MaxValue))
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-26} {1,8:F2} {2,-6} {3,9} {4,8}  {5}",
                    c.Id, c.MeasuredKbps, c.Legal ? "yes" : (c.Fail == "" ? "no" : c.Fail),
                    double.IsNaN(c.MeanNmr) ? "-" : c.MeanNmr.ToString("F3", CultureInfo.InvariantCulture),
                    double.IsNaN(c.AudFrac) ? "-" : c.AudFrac.ToString("F3", CultureInfo.InvariantCulture),
                    string.Join(" ", c.Args)));
        }

        if (win == null)
        {
            Console.Error.WriteLine($"\nNO candidate landed in [{target - tolerance:F1}, {target:F1}] kbps." +
                " For very short clips discrete frame sizes can make the window unreachable; widen --tolerance or use --scope file.");
            TryCleanup(work);
            return strict ? 1 : 3;
        }

        File.Copy(Path.Combine(work, win.Id, "c.mp3"), output, true);
        Console.WriteLine();
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "WINNER {0}: {1:F2} kbps, meanNMR {2:F3} dB, audFrac {3:F3}\n  lame {4}\n  -> {5}",
            win.Id, win.MeasuredKbps, win.MeanNmr, win.AudFrac, string.Join(" ", win.Args), output));
        TryCleanup(work);
        return 0;
    }

    // The candidate grid, all standards-compliant (design doc, "Candidate grid"). One dimension
    // varies at a time from an ABR-at-target base so the grid stays ~17 strong instead of a
    // full cross-product; the measured score picks, we never hardcode which dimension matters.
    static List<Candidate> BuildGrid(bool mono, int t)
    {
        var g = new List<Candidate>();
        string mode = mono ? "-m m" : "-m j";
        void Add(string id, string a, bool vbr = false, int filters = 0) =>
            g.Add(new Candidate { Id = id, Args = a.Split(' '), IsVbr = vbr, Filters = filters });
        string[] lpsFor(bool m) => m ? new[] { "12", "14", "15.5", "16" } : new[] { "16", "17", "18", "19" };

        // Effort sweep at the target mean. q0 carries the Finding 1 fix in this repo's build;
        // --quality-max is the Finding 2/3 maximum-effort mode; -q4 stays as the stock-safe
        // baseline so the tool also behaves sanely when pointed (--lame) at an unfixed build.
        Add("abr-q4", $"{mode} --abr {t} -q 4");
        Add("abr-q0", $"{mode} --abr {t} -q 0");
        Add("abr-qmax", $"{mode} --abr {t} --quality-max");

        // Mean nudges BOTH ways, contiguous: ABR undershoots on easy material (a cappella
        // measured 189 for --abr 192) and can overshoot on hot material. Requests are integer
        // kbps and move the measured average ~0.8 kbps per step, so gaps in the sweep can strand
        // a 0.5-wide window between two requests. Only the MEASURED average decides legality.
        foreach (int d in new[] { -2, -1, 1, 2, 3, 4, 5, 6 })
            Add($"abr{(d > 0 ? "+" : "")}{d}-qmax", $"{mode} --abr {t + d} --quality-max");

        // CBR at the target: the guaranteed-legal floor. CBR's padding maintains EXACTLY the
        // nominal rate (measures 96.00/192.00), always inside the window and using the whole
        // budget, with the reservoir still active. ABR usually beats it at equal average by
        // moving bits between moments -- but when no integer ABR request lands in the window
        // (it happens: the window is narrower than the request granularity), CBR is the honest
        // fallback rather than a failure. Include the lowpass sweep here too, since on speech
        // it was measured to be the best single lever.
        Add("cbr-q4", $"{mode} -b {t} -q 4");
        Add("cbr-qmax", $"{mode} -b {t} --quality-max");
        foreach (var lp in lpsFor(mono))
            Add($"cbr-lp{lp}-qmax", $"{mode} -b {t} --quality-max --lowpass {lp}", filters: 1);

        // Lowpass: trading inaudible top octave for cleaner mids is often the best deal at a
        // fixed budget -- but only measurement may decide that, so "auto" (no flag) is the base.
        foreach (var lp in lpsFor(mono))
            Add($"abr-lp{lp}-qmax", $"{mode} --abr {t} --quality-max --lowpass {lp}", filters: 1);

        // Highpass: speech-heavy material wastes bits on rumble below the voice band.
        var hps = mono ? new[] { "50", "70", "90" } : new[] { "40", "60", "80" };
        foreach (var hp in hps)
            Add($"abr-hp{hp}-qmax", $"{mode} --abr {t} --quality-max --highpass {hp}", filters: 1);

        // True-VBR probes: better bit placement than ABR when the landing happens to be legal.
        // Purely opportunistic -- rejected unless the MEASURED average is in the window.
        var vs = mono ? new[] { "4.0", "4.5", "5.0", "5.5" } : new[] { "2.0", "2.5", "3.0", "3.5" };
        foreach (var v in vs)
            Add($"vbr-V{v}", $"{mode} -V {v}", vbr: true);

        return g;
    }

    // Average bitrate over the audio frames themselves: walk MPEG frame headers, sum bytes and
    // frame durations. Includes the Xing/LAME info frame (it is a real stream frame and is the
    // same ~200 bytes for every candidate, so it cancels in comparison). ID3v2 is skipped;
    // trailing ID3v1/tags never match a sync word, so the walk ends cleanly at stream end.
    static double AudioFrameKbps(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int pos = 0;
        if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            pos = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        // MPEG1 Layer III tables (this tool only ever drives 32/44.1/48 kHz input).
        int[] br = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 };
        int[] sr = { 44100, 48000, 32000, -1 };
        long bytes = 0; double seconds = 0;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int ver = (b[pos + 1] >> 3) & 3, layer = (b[pos + 1] >> 1) & 3;
            if (ver != 3 || layer != 1) { pos++; continue; }   // MPEG1 Layer III only
            int bi = (b[pos + 2] >> 4) & 15, si = (b[pos + 2] >> 2) & 3, pad = (b[pos + 2] >> 1) & 1;
            if (br[bi] <= 0 || sr[si] <= 0) { pos++; continue; }
            int flen = 144000 * br[bi] / sr[si] + pad;
            bytes += flen; seconds += 1152.0 / sr[si];
            pos += flen;
        }
        return seconds > 0 ? bytes * 8.0 / seconds / 1000.0 : -1;
    }

    static double FileScopeKbps(string path)
    {
        // Whole file (tags included) over audio duration -- what a hosting bill sees.
        double sec = 0; byte[] b = File.ReadAllBytes(path);
        double audio = AudioFrameKbps(path);
        if (audio < 0) return -1;
        // Recover duration from the audio-scope walk: kbps = bits/sec/1000 => sec = bits/(kbps*1000).
        // Cheaper than re-walking: derive from audio bytes. Re-walk instead for exactness:
        sec = DurationSeconds(path);
        return sec > 0 ? b.Length * 8.0 / sec / 1000.0 : -1;
    }

    static double DurationSeconds(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        int pos = 0;
        if (b.Length > 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            pos = 10 + ((b[6] & 0x7F) << 21 | (b[7] & 0x7F) << 14 | (b[8] & 0x7F) << 7 | (b[9] & 0x7F));
        int[] br = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, -1 };
        int[] sr = { 44100, 48000, 32000, -1 };
        double seconds = 0;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            int ver = (b[pos + 1] >> 3) & 3, layer = (b[pos + 1] >> 1) & 3;
            if (ver != 3 || layer != 1) { pos++; continue; }
            int bi = (b[pos + 2] >> 4) & 15, si = (b[pos + 2] >> 2) & 3, pad = (b[pos + 2] >> 1) & 1;
            if (br[bi] <= 0 || sr[si] <= 0) { pos++; continue; }
            seconds += 1152.0 / sr[si];
            pos += 144000 * br[bi] / sr[si] + pad;
        }
        return seconds;
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
