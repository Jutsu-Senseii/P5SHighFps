// P5SScan - developer tool: finds frame-locked counters in Persona 5 Strikers memory (e.g. the eye blink timer)
// and the instructions that write them. Scan/watch modes are read-only; --trace injects a small exception handler.
//
// Stage 1: writable memory is read in blocks, three times a few tens of ms apart; int32/float32 values changing at
//          the frame rate (+1/-1 or +-1/60 per frame) are kept.
// Stage 2: candidates are sampled for N seconds; counters that loop (reset) are listed first, with their period.
// Triggered with F9 in game (the game freezes when it loses focus); beeps at start and end.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

const double FrameRate = 120;
double[] RatesInt = [FrameRate, -FrameRate];
double[] RatesFloat = [FrameRate, -FrameRate, FrameRate / 60, -FrameRate / 60];
int sampleSeconds = 12;
string outDir = AppContext.BaseDirectory;
string? watchFile = null;
foreach (var a in args)
{
    if (a.StartsWith("--seconds=")) sampleSeconds = int.Parse(a[10..]);
    else if (a.StartsWith("--watch=")) watchFile = a[8..];
}

var traceArg = args.FirstOrDefault(a => a.StartsWith("--trace="));
if (traceArg != null)
{
    var tlog = new StreamWriter(Path.Combine(outDir, "trace.log"), append: true) { AutoFlush = true };
    void TLog(string s) { Console.WriteLine(s); tlog.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); }
    var targets = traceArg[8..].Split(',').Select(s => Convert.ToInt64(s, 16)).ToArray();
    return Tracer.Run(targets, sampleSeconds, args.Contains("--exec"), TLog, WaitF9);
}

var proc = Process.GetProcessesByName("game").FirstOrDefault();
if (proc == null) { Console.WriteLine("game.exe not found"); return 1; }
nint h = Native.OpenProcess(0x0410, false, proc.Id);
long baseAddr = (long)proc.MainModule!.BaseAddress;
var log = new StreamWriter(Path.Combine(outDir, watchFile == null ? "scan.log" : "watch.log")) { AutoFlush = true };
void Log(string s) { Console.WriteLine(s); log.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}"); }

Console.WriteLine("Get into the situation to analyse in game, then press F9.");
WaitF9();
Console.Beep(1200, 150);

List<(long Addr, bool IsFloat)> cands;
if (watchFile == null)
{
    cands = Stage1();
    Log($"Stage 1: {cands.Count} candidates");
}
else
{
    cands = File.ReadAllLines(watchFile).Where(l => l.Length > 0 && !l.StartsWith('#'))
        .Select(l => l.Split(' ')).Select(p => (Convert.ToInt64(p[0], 16), p[1] == "f")).ToList();
    Log($"Watching {cands.Count} addresses from {watchFile}");
}
Stage2(cands);
Console.Beep(900, 150); Console.Beep(1200, 150);
Log("Done.");
return 0;

void WaitF9()
{
    bool was = (Native.GetAsyncKeyState(0x78) & 0x8000) != 0;
    while (true)
    {
        Thread.Sleep(20);
        bool down = (Native.GetAsyncKeyState(0x78) & 0x8000) != 0;
        if (down && !was) return;
        was = down;
    }
}

List<(long, long)> WritableRanges()
{
    var list = new List<(long, long)>();
    long a = 0;
    while (Native.VirtualQueryEx(h, (nint)a, out var m, Marshal.SizeOf<Native.MBI>()) != 0)
    {
        long size = m.RegionSize;
        if (m.State == 0x1000 && (m.Protect == 0x04 || m.Protect == 0x40) && (m.Protect & 0x100) == 0)
            for (long o = 0; o < size; o += 16 << 20)
                list.Add(((long)m.BaseAddress + o, Math.Min(16 << 20, size - o)));
        a = (long)m.BaseAddress + size;
        if (a <= 0) break;
    }
    return list;
}

// Rates looked for (per second, at 120 FPS): +1 or -1 per frame (int or float), and +-1/60 per frame
// (seconds computed assuming 60 FPS).
bool Near(double d, double dt, double rate) { double k = rate * dt; return Math.Abs(k) > 1e-6 && d / k >= 0.6 && d / k <= 1.6; }
bool PlausibleInt(double d1, double dt1, double d2, double dt2) => RatesInt.Any(r => Math.Abs(r * dt1) >= 2 && Near(d1, dt1, r) && Near(d2, dt2, r));
bool PlausibleFloat(double d1, double dt1, double d2, double dt2) => RatesFloat.Any(r => Near(d1, dt1, r) && Near(d2, dt2, r));

List<(long, bool)> Stage1()
{
    var ranges = WritableRanges();
    Log($"Stage 1: {ranges.Count} blocks, {ranges.Sum(r => r.Item2) >> 20} MB");
    var result = new List<(long, bool)>();
    var sw = Stopwatch.StartNew();
    const long GroupBytes = 192 << 20;
    int i = 0;
    while (i < ranges.Count)
    {
        var group = new List<(long Addr, long Size)>();
        long sum = 0;
        while (i < ranges.Count && sum < GroupBytes) { group.Add(ranges[i]); sum += ranges[i].Item2; i++; }
        var snaps = new byte[3][][];
        var times = new double[3][];
        for (int s = 0; s < 3; s++)
        {
            snaps[s] = new byte[group.Count][];
            times[s] = new double[group.Count];
            for (int g = 0; g < group.Count; g++)
            {
                var buf = new byte[group[g].Size];
                times[s][g] = sw.Elapsed.TotalSeconds;
                if (!Native.ReadProcessMemory(h, (nint)group[g].Addr, buf, (nint)buf.Length, out _)) buf = [];
                snaps[s][g] = buf;
            }
            if (s < 2) Thread.Sleep(40);
        }
        for (int g = 0; g < group.Count; g++)
        {
            var A = snaps[0][g]; var B = snaps[1][g]; var C = snaps[2][g];
            if (A.Length == 0 || B.Length != A.Length || C.Length != A.Length) continue;
            double dt1 = times[1][g] - times[0][g], dt2 = times[2][g] - times[1][g];
            var ia = MemoryMarshal.Cast<byte, int>(A); var ib = MemoryMarshal.Cast<byte, int>(B); var ic = MemoryMarshal.Cast<byte, int>(C);
            var fa = MemoryMarshal.Cast<byte, float>(A); var fb = MemoryMarshal.Cast<byte, float>(B); var fc = MemoryMarshal.Cast<byte, float>(C);
            for (int k = 0; k < ia.Length; k++)
            {
                int x = ia[k], y = ib[k], z = ic[k];
                if (x != y && x > -1_000_000 && x < 1_000_000 && PlausibleInt(y - x, dt1, z - y, dt2))
                    result.Add((group[g].Addr + k * 4, false));
                else
                {
                    float p = fa[k], q = fb[k], r = fc[k];
                    if (p != q && float.IsFinite(p) && float.IsFinite(r) && Math.Abs(p) < 1e6f && Math.Abs(p) > 1e-4f && PlausibleFloat(q - p, dt1, r - q, dt2))
                        result.Add((group[g].Addr + k * 4, true));
                }
            }
        }
        if (result.Count > 3_000_000) { Log("Too many candidates, stage 1 stopped early."); break; }
    }
    Log($"Stage 1 done in {sw.Elapsed.TotalSeconds:F1} s");
    return result;
}

void Stage2(List<(long Addr, bool IsFloat)> cands)
{
    // Group candidates into contiguous ranges (one read per range)
    var sorted = cands.OrderBy(c => c.Addr).ToList();
    var spans = new List<(long Start, int Len, int First, int Count)>();
    for (int i = 0; i < sorted.Count;)
    {
        long start = sorted[i].Addr; int j = i;
        while (j + 1 < sorted.Count && sorted[j + 1].Addr - sorted[j].Addr < 256 && sorted[j + 1].Addr - start < 65536) j++;
        spans.Add((start, (int)(sorted[j].Addr - start + 4), i, j - i + 1));
        i = j + 1;
    }
    Log($"Stage 2: {sorted.Count} candidates in {spans.Count} ranges, sampling for {sampleSeconds} s");
    // Streaming statistics (samples are not stored)
    int n = sorted.Count;
    // Normal steps (regular signed change) vs jumps (counter reset or countdown reload): a jump is a change larger
    // than 8x the usual change (moving average).
    var prev = new float[n]; var prevT = new double[n]; var rise = new double[n]; var riseT = new double[n];
    var max = new double[n]; var min = new double[n]; var resets = new int[n]; var firstReset = new double[n]; var lastReset = new double[n];
    var ema = new double[n];
    var bad = new bool[n];
    var sw = Stopwatch.StartNew();
    var buf = new byte[65536 + 8];
    int sampleCount = 0;
    while (sw.Elapsed.TotalSeconds < sampleSeconds)
    {
        foreach (var sp in spans)
        {
            double t = sw.Elapsed.TotalSeconds;
            bool ok = Native.ReadProcessMemory(h, (nint)sp.Start, buf, sp.Len, out _);
            for (int k = 0; k < sp.Count; k++)
            {
                int c = sp.First + k;
                if (!ok) { bad[c] = true; continue; }
                int off = (int)(sorted[c].Addr - sp.Start);
                float b = sorted[c].IsFloat ? BitConverter.ToSingle(buf, off) : BitConverter.ToInt32(buf, off);
                if (sampleCount > 0)
                {
                    double d = b - prev[c];
                    if (ema[c] > 0 && Math.Abs(d) > 8 * ema[c])
                    { if (resets[c] == 0) firstReset[c] = t; lastReset[c] = t; resets[c]++; }
                    else
                    {
                        rise[c] += d; riseT[c] += t - prevT[c];
                        ema[c] = ema[c] == 0 ? Math.Abs(d) : 0.9 * ema[c] + 0.1 * Math.Abs(d);
                    }
                }
                else { max[c] = b; min[c] = b; }
                if (b > max[c]) max[c] = b;
                if (b < min[c]) min[c] = b;
                prev[c] = b; prevT[c] = t;
            }
        }
        sampleCount++;
        Thread.Sleep(10);
    }
    Log($"{sampleCount} samples (average interval {sampleSeconds * 1000.0 / sampleCount:F0} ms)");

    var rows = new List<(long Addr, bool F, double Rate, int Resets, double Period, double Min, double Max)>();
    for (int c = 0; c < n; c++)
    {
        if (bad[c] || riseT[c] < 1) continue;
        double period = resets[c] >= 2 ? (lastReset[c] - firstReset[c]) / (resets[c] - 1) : 0;
        rows.Add((sorted[c].Addr, sorted[c].IsFloat, rise[c] / riseT[c], resets[c], period, min[c], max[c]));
    }
    string Mod(long a) => a >= baseAddr && a < baseAddr + 0x1B2F2000 ? $"game.exe+{a - baseAddr:X}" : $"{a:X}";
    var sb = new StringBuilder();
    sb.AppendLine("# address type rate/s jumps period_s min max");
    // Looping timers (at least 2 jumps) first
    foreach (var r in rows.OrderByDescending(r => r.Resets >= 2).ThenBy(r => r.Period))
        sb.AppendLine($"{r.Addr:X} {(r.F ? "f" : "i")} {r.Rate:F2} {r.Resets} {r.Period:F2} {r.Min:G5} {r.Max:G5} {Mod(r.Addr)}");
    var file = Path.Combine(outDir, watchFile == null ? "candidates.txt" : "watch_result.txt");
    File.WriteAllText(file, sb.ToString());
    int looping = rows.Count(r => r.Resets >= 2);
    Log($"{rows.Count} counters analysed, {looping} looping. Results: {file}");
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MBI { public nint BaseAddress, AllocationBase; public uint AllocationProtect; public ushort PartitionId; public long RegionSize; public uint State, Protect, Type; }
    [DllImport("kernel32", SetLastError = true)] public static extern nint OpenProcess(uint a, bool i, int p);
    [DllImport("kernel32", SetLastError = true)] public static extern bool ReadProcessMemory(nint h, nint a, byte[] b, nint n, out nint r);
    [DllImport("kernel32", SetLastError = true)] public static extern nint VirtualQueryEx(nint h, nint a, out MBI m, int l);
    [DllImport("user32")] public static extern short GetAsyncKeyState(int vk);
}
