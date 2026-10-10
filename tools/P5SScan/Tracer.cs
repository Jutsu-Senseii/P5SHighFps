// --trace mode: finds the instructions that write (or, with --exec, execute) up to four addresses, without a debugger.
// A small vectored exception handler is injected into the game.exe code cave (page made writable by P5SHighFps, which
// must be running); hardware breakpoints (DR0-DR3) are set on every thread for a few seconds; each hit raises a
// single-step exception that the handler records (RIP, xmm0/xmm1, 16 stack qwords) and dismisses.

using System.Diagnostics;
using System.Runtime.InteropServices;

static class Tracer
{
    const long Base = 0x140000000;
    const long Handler = Base + 0x96400, RegStub = Base + 0x96500, VehHandle = Base + 0x96580;
    const long Count = Base + 0x96588, Buf = Base + 0x97000; // Buf: 64 entries of 0xA0 bytes (pages 0x97000-0x99FFF)
    const int EntrySize = 0xA0, BufPages = 0x3000;
    // v1 (older builds) and v2: v2 also records xmm1, xmm0 and 16 stack qwords, and sets RF so execution can resume
    // after an execute breakpoint.
    const string HandlerV1Hex = "488b01813804000080754d488b4108488b50684c8b80f800000041b90100000049bb8865094001000000f0450fc10b4183e13f49c1e10449ba00660940010000004f89040a4b89540a0848c7406800000000b8ffffffffc331c0c3";
    const string HandlerHex = "488b018138040000800f8587000000488b41088148440000010041b90100000049bb8865094001000000f0450fc10b4183e13f4d69c9a000000049ba00700940010000004d01ca4c8b80f80000004d89024c8b40684d894208448b80b001000045894210448b80a001000045894214488b909800000031c94c8b04ca4d8944ca18ffc183f91072f048c7406800000000b8ffffffffc331c0c3";
    const string RegHex = "4883ec28b90100000048ba006409400100000048b88877665544332211ffd049bb80650940010000004989034883c428c3";

    public static int Run(long[] targets, int seconds, bool exec, Action<string> log, Action waitF9)
    {
        var proc = Process.GetProcessesByName("game").First();
        if ((long)proc.MainModule!.BaseAddress != Base) { log("Unexpected game.exe base address"); return 2; }
        nint h = OpenProcess(0x0002 | 0x0008 | 0x0010 | 0x0020 | 0x0400, false, proc.Id);

        byte[] handler = Convert.FromHexString(HandlerHex);
        byte[] v1 = Convert.FromHexString(HandlerV1Hex);
        byte[] cur = Read(h, Handler, handler.Length);
        byte[] bufZone = Read(h, Buf, BufPages);
        if (!VirtualProtectEx(h, (nint)Buf, BufPages, 0x40, out _)) { log("Could not make the record buffer writable."); return 7; }
        if (cur.All(b => b == 0xCC))
        {
            if (Read(h, Base + 0x96400, 0x200).Any(b => b != 0xCC)) { log("Code cave area not empty, aborting."); return 3; }
            nint ntdll = GetModuleHandle("ntdll.dll");
            long addVeh = GetProcAddress(ntdll, "RtlAddVectoredExceptionHandler");
            // ntdll is mapped at the same address in every process of the session
            if (!proc.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) && m.BaseAddress == ntdll))
            { log("ntdll is at a different address in the game, aborting."); return 4; }
            byte[] reg = Convert.FromHexString(RegHex);
            int ph = IndexOf(reg, BitConverter.GetBytes(0x1122334455667788L));
            BitConverter.GetBytes(addVeh).CopyTo(reg, ph);
            Write(h, Handler, handler);
            Write(h, RegStub, reg);
            Write(h, VehHandle, new byte[16]);
            Write(h, Buf, new byte[64 * EntrySize]);
            nint th = CreateRemoteThread(h, 0, 0, (nint)RegStub, 0, 0, out _);
            WaitForSingleObject(th, 5000);
            CloseHandle(th);
            long veh = BitConverter.ToInt64(Read(h, VehHandle, 8));
            log($"Exception handler installed (handle 0x{veh:X})");
            if (veh == 0) return 5;
        }
        else if (cur.SequenceEqual(handler) || cur.AsSpan(0, v1.Length).SequenceEqual(v1))
        {
            // No breakpoint is active here: the handler can be rewritten safely.
            Write(h, Handler, handler);
            Write(h, Count, new byte[8]);
            Write(h, Buf, new byte[64 * EntrySize]);
            log("Handler already present, updated.");
        }
        else { log("Handler area used by something else, aborting."); return 6; }

        log($"Press F9 in game to watch {string.Join(", ", targets.Select(t => t.ToString("X")))} for {seconds} s.");
        waitF9();
        Console.Beep(1200, 150);
        long dr7 = 0;
        for (int i = 0; i < targets.Length && i < 4; i++)
            dr7 |= exec ? (1L << (i * 2)) // Li, RWi=00 (execute), LENi=00
                        : (1L << (i * 2)) | (1L << (16 + i * 4)) | (3L << (18 + i * 4)); // Li, RWi=01 (write), LENi=11
        int set = SetAll(proc, targets, dr7);
        log($"Breakpoints set on {set} threads");
        Thread.Sleep(seconds * 1000);
        int cleared = SetAll(proc, [], 0);
        log($"Breakpoints cleared on {cleared} threads");
        Console.Beep(900, 150); Console.Beep(1200, 150);

        int hits = BitConverter.ToInt32(Read(h, Count, 4));
        byte[] buf = Read(h, Buf, 64 * EntrySize);
        string Where(long a) => a >= Base && a < Base + 0x1B2F2000 ? $"+{a - Base:X}" : $"{a:X}";
        log($"{hits} hits recorded");
        if (!exec)
        {
            var rips = new Dictionary<long, int>();
            for (int i = 0; i < Math.Min(hits, 64); i++)
            {
                long rip = BitConverter.ToInt64(buf, i * EntrySize);
                rips[rip] = rips.GetValueOrDefault(rip) + 1;
            }
            foreach (var (rip, n) in rips.OrderByDescending(kv => kv.Value))
                log($"  RIP game.exe{Where(rip)} ({n}x) preceding bytes: {Convert.ToHexString(Read(h, rip - 24, 32))}");
        }
        else
        {
            // Group by (caller, xmm1 value), with the code addresses found on the stack.
            var groups = new Dictionary<string, int>();
            for (int i = 0; i < Math.Min(hits, 64); i++)
            {
                int o = i * EntrySize;
                float x1 = BitConverter.ToSingle(buf, o + 0x10);
                var codes = new List<string>();
                for (int k = 0; k < 16; k++)
                {
                    long v = BitConverter.ToInt64(buf, o + 0x18 + k * 8);
                    if (v > Base + 0x1000 && v < Base + 0x1B183000) codes.Add($"[{k * 8:X}]{Where(v)}");
                }
                string key = $"xmm1={x1:G6} stack: {string.Join(" ", codes.Take(6))}";
                groups[key] = groups.GetValueOrDefault(key) + 1;
            }
            foreach (var (k, n) in groups.OrderByDescending(kv => kv.Value)) log($"  ({n}x) {k}");
            // xmm1 values in chronological order (last 64)
            var seq = new List<string>();
            int count = Math.Min(hits, 64);
            for (int j = 0; j < count; j++)
            {
                int idx = (hits - count + j) & 63;
                seq.Add(BitConverter.ToSingle(buf, idx * EntrySize + 0x10).ToString("G5"));
            }
            log("  xmm1 timeline: " + string.Join(" ", seq));
            log($"  Rate: {hits / (double)seconds:F0} hits/s");
        }
        return 0;
    }

    static int SetAll(Process proc, long[] targets, long dr7)
    {
        proc.Refresh();
        int n = 0;
        nint ctx = Marshal.AllocHGlobal(0x4D0 + 16);
        nint aligned = (ctx + 15) & ~(nint)15;
        try
        {
            foreach (ProcessThread t in proc.Threads)
            {
                nint th = OpenThread(0x0002 | 0x0008 | 0x0010, false, t.Id);
                if (th == 0) continue;
                if (SuspendThread(th) != uint.MaxValue)
                {
                    for (int i = 0; i < 0x4D0; i += 8) Marshal.WriteInt64(aligned, i, 0);
                    Marshal.WriteInt32(aligned, 0x30, 0x00100010); // CONTEXT_DEBUG_REGISTERS
                    if (GetThreadContext(th, aligned))
                    {
                        for (int i = 0; i < 4; i++) Marshal.WriteInt64(aligned, 0x48 + i * 8, i < targets.Length ? targets[i] : 0);
                        Marshal.WriteInt64(aligned, 0x68, 0);
                        Marshal.WriteInt64(aligned, 0x70, dr7);
                        if (SetThreadContext(th, aligned)) n++;
                    }
                    ResumeThread(th);
                }
                CloseHandle(th);
            }
        }
        finally { Marshal.FreeHGlobal(ctx); }
        return n;
    }

    static int IndexOf(byte[] a, byte[] p)
    {
        for (int i = 0; i <= a.Length - p.Length; i++) if (a.AsSpan(i, p.Length).SequenceEqual(p)) return i;
        return -1;
    }
    static byte[] Read(nint h, long a, int n) { var b = new byte[n]; ReadProcessMemory(h, (nint)a, b, n, out _); return b; }
    static void Write(nint h, long a, byte[] b) { if (!WriteProcessMemory(h, (nint)a, b, b.Length, out _)) throw new System.ComponentModel.Win32Exception(); }

    [DllImport("kernel32", SetLastError = true)] static extern nint OpenProcess(uint a, bool i, int p);
    [DllImport("kernel32", SetLastError = true)] static extern nint OpenThread(uint a, bool i, int id);
    [DllImport("kernel32", SetLastError = true)] static extern bool ReadProcessMemory(nint h, nint a, byte[] b, int n, out nint r);
    [DllImport("kernel32", SetLastError = true)] static extern bool VirtualProtectEx(nint h, nint a, nint n, uint p, out uint o);
    [DllImport("kernel32", SetLastError = true)] static extern bool WriteProcessMemory(nint h, nint a, byte[] b, int n, out nint w);
    [DllImport("kernel32", SetLastError = true)] static extern nint CreateRemoteThread(nint h, nint sa, nint ss, nint start, nint param, uint flags, out uint id);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32")] static extern uint SuspendThread(nint h);
    [DllImport("kernel32")] static extern uint ResumeThread(nint h);
    [DllImport("kernel32", SetLastError = true)] static extern bool GetThreadContext(nint h, nint ctx);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetThreadContext(nint h, nint ctx);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern long GetProcAddress(nint m, string name);
}
