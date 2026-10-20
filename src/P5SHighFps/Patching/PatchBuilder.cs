using P5SHighFps.Game;
using P5SHighFps.Native;

namespace P5SHighFps.Patching;

/// <summary>
/// Builds the list of patches. Every site is checked against the expected instruction bytes first; a site that does
/// not match is skipped and logged, so nothing unexpected is ever overwritten.
/// </summary>
sealed class PatchBuilder(RemoteProcess mem, long baseAddr, CodeCave cave)
{
    public List<Patch> Patches { get; } = [];
    /// <summary>Diagnostics only: divisor readers that the bisection can force to 1.</summary>
    public List<Patch> BisectionCandidates { get; } = [];

    /// <summary>Simulation step sites: each "cvtsi2ss xmmN, r64" becomes a call to the thunk loading the step.</summary>
    public void AddStepSites()
    {
        foreach (var (reg, rvas) in Addresses.StepSites)
            foreach (var rva in rvas)
            {
                long addr = baseAddr + rva;
                byte[] orig = mem.Read(addr, 5);
                if (!IsCvtsi2ss(orig, reg))
                {
                    Log.Info($"  SKIPPED  game.exe+{rva:X}: unexpected bytes {Convert.ToHexString(orig)}");
                    continue;
                }
                var family = Addresses.RelocatedTraceSites.Contains(rva) ? Family.RelocatedTraceSites
                    : rva == Addresses.SecondsPerFrameSite ? Family.SecondsPerFrame
                    : Family.CheatTableSites;
                Patches.Add(new Patch(addr, orig, Call(addr, cave.Thunk(reg)), family));
            }
        int stepSites = Patches.Count;
        // SetFrameRate redirected to the cave replacement (verified by the caller before anything is written)
        long setter = baseAddr + Addresses.SetFrameRate;
        Patches.Add(new Patch(setter, Addresses.SetFrameRateOrig[..5], Jmp(setter, cave.SetFrameRateStub), Family.AlwaysOn));
        Log.Info($"{stepSites}/{Addresses.StepSites.Sum(s => s.Rvas.Length)} simulation step sites verified.");
    }

    public void AddUiFixedStep()
    {
        long addr = baseAddr + Addresses.UiFixedStep;
        if (mem.Read(addr, 5).SequenceEqual(Addresses.UiFixedStepOrig))
        {
            Patches.Add(new Patch(addr, Addresses.UiFixedStepOrig, Call(addr, cave.UiStepStub), Family.UiAnimations));
            Log.Info("UI fixed-step animations: step scaled.");
        }
        else Log.Info($"  SKIPPED  UI animations game.exe+{Addresses.UiFixedStep:X}: unexpected bytes");
    }

    /// <summary>Short looping motions: their time is snapped to whole frames (see <see cref="Addresses.MotionTime"/>).</summary>
    public void AddShortMotions()
    {
        long addr = baseAddr + Addresses.MotionTime;
        if (mem.Read(addr, 6).SequenceEqual(Addresses.MotionTimeOrig))
        {
            Patches.Add(new Patch(addr, Addresses.MotionTimeOrig, [.. Jmp(addr, cave.ShortMotionStub), 0x90], Family.ShortMotions));
            Log.Info("Short looping motions and effects: time snapped to whole frames.");
        }
        else Log.Info($"  SKIPPED  short motions game.exe+{Addresses.MotionTime:X}: unexpected bytes");
    }

    /// <summary>Gauge durations scaled by target / 60 (see <see cref="Addresses.GaugeDurationSites"/>).</summary>
    public void AddGaugeDurations()
    {
        int scaled = 0;
        foreach (var rva in Addresses.GaugeDurationSites)
        {
            long addr = baseAddr + rva;
            if (!mem.Read(addr, 7).SequenceEqual(Addresses.GaugeDurationOrig))
            {
                Log.Info($"  SKIPPED  gauge game.exe+{rva:X}: unexpected bytes");
                continue;
            }
            Patches.Add(new Patch(addr, Addresses.GaugeDurationOrig, [.. Call(addr, cave.GaugeStub), 0x66, 0x90], Family.Gauges));
            scaled++;
        }
        Log.Info($"Gauges: {scaled}/{Addresses.GaugeDurationSites.Length} durations scaled by the frame rate.");
    }

    /// <summary>Lock-on camera: its dead zone is scaled by the step (see <see cref="Addresses.LockOnDeadZone"/>).</summary>
    public void AddLockOnCamera()
    {
        long addr = baseAddr + Addresses.LockOnDeadZone;
        if (mem.Read(addr, 5).SequenceEqual(Addresses.LockOnDeadZoneOrig))
        {
            Patches.Add(new Patch(addr, Addresses.LockOnDeadZoneOrig, Call(addr, cave.LockOnStub), Family.LockOnCamera));
            Log.Info("Lock-on camera: dead zone scaled by the step.");
        }
        else Log.Info($"  SKIPPED  lock-on camera game.exe+{Addresses.LockOnDeadZone:X}: unexpected bytes");
    }

    /// <summary>The once-per-frame anchor: frame counting only, or integer ticks as well. False if it does not match.</summary>
    public bool AddFrameAnchor(bool intTicks)
    {
        long addr = baseAddr + Addresses.FrameAnchor;
        if (!mem.Read(addr, 6).SequenceEqual(Addresses.FrameAnchorOrig)) return false;
        var call = new byte[6];
        Call(addr, intTicks ? cave.AnchorTicksStub : cave.AnchorCountStub).CopyTo(call, 0);
        call[5] = 0x90; // nop: the original instruction is 6 bytes long
        Patches.Add(new Patch(addr, Addresses.FrameAnchorOrig, call, intTicks ? Family.IntegerCounters : Family.AlwaysOn));
        return true;
    }

    /// <summary>Integer counters: calls that must never see a divisor of 0, and the eye blink controller.</summary>
    public void AddIntegerCounters()
    {
        int redirected = 0;
        foreach (var rva in Addresses.FixedOneSites)
        {
            long addr = baseAddr + rva;
            byte[] orig = mem.Read(addr, 5);
            if (!IsDivisorGetterCall(addr, orig))
            {
                Log.Info($"  SKIPPED  call game.exe+{rva:X}: unexpected bytes {Convert.ToHexString(orig)}");
                continue;
            }
            Patches.Add(new Patch(addr, orig, Call(addr, cave.ReturnOneStub), Family.IntegerCounters));
            redirected++;
        }
        Log.Info($"Integer counters: {redirected}/{Addresses.FixedOneSites.Length} calls redirected to a fixed divisor of 1.");

        long blink = baseAddr + Addresses.BlinkFrames;
        if (mem.Read(blink, 5).SequenceEqual(Addresses.BlinkFramesOrig))
        {
            Patches.Add(new Patch(blink, Addresses.BlinkFramesOrig, Call(blink, cave.BlinkStub), Family.IntegerCounters));
            Log.Info("Eye blinking: frame step replaced by the divisor.");
        }
        else Log.Info($"  SKIPPED  eye blinking game.exe+{Addresses.BlinkFrames:X}: unexpected bytes");
    }

    /// <summary>Diagnostics: every other divisor getter call and the two direct reads, as bisection candidates.</summary>
    public void AddBisectionCandidates()
    {
        foreach (var rva in Addresses.BisectGetterSites)
        {
            if (Addresses.FixedOneSites.Contains(rva)) continue; // already forced to 1 permanently
            long addr = baseAddr + rva;
            byte[] orig = mem.Read(addr, 5);
            if (!IsDivisorGetterCall(addr, orig)) continue;
            BisectionCandidates.Add(new Patch(addr, orig, Call(addr, cave.ReturnOneStub), Family.IntegerCounters,
                $"call game.exe+{rva:X}"));
        }
        // Direct reads: their disp32 is redirected to a constant 1 inside the cave
        foreach (var (rva, len) in new[] { (Addresses.DirectRead1, 6), (Addresses.DirectRead2, 7) })
        {
            long addr = baseAddr + rva;
            byte[] orig = mem.Read(addr, len);
            if (addr + len + BitConverter.ToInt32(orig, len - 4) != baseAddr + Addresses.Divisor) continue;
            byte[] replacement = (byte[])orig.Clone();
            CodeCave.Rel32(addr + len, cave.ConstOne).CopyTo(replacement, len - 4);
            BisectionCandidates.Add(new Patch(addr, orig, replacement, Family.IntegerCounters, $"direct read game.exe+{rva:X}"));
        }
        Log.Info($"Diagnostics: bisection over {BisectionCandidates.Count} divisor readers available (F5/F6).");
    }

    /// <summary>"cvtsi2ss xmmN, r64" with the expected destination register.</summary>
    static bool IsCvtsi2ss(byte[] b, int reg)
    {
        if (b[0] != 0xF3 || b[2] != 0x0F || b[3] != 0x2A) return false;
        int rexR = b[1] switch { 0x48 => 0, 0x4C => 8, _ => -1 };
        if (rexR < 0 || (b[4] >> 6) != 3) return false;
        return ((b[4] >> 3) & 7) + rexR == reg;
    }

    /// <summary>"call rel32" to the divisor getter or its thunk.</summary>
    bool IsDivisorGetterCall(long addr, byte[] b)
    {
        long target = b[0] == 0xE8 ? addr + 5 + BitConverter.ToInt32(b, 1) : 0;
        return target == baseAddr + Addresses.GetDivisorThunk || target == baseAddr + Addresses.GetDivisor;
    }

    static byte[] Call(long from, long target) => [0xE8, .. CodeCave.Rel32(from + 5, target)];
    static byte[] Jmp(long from, long target) => [0xE9, .. CodeCave.Rel32(from + 5, target)];
}
