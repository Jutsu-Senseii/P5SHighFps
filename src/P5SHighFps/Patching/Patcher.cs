using P5SHighFps.Game;
using P5SHighFps.Native;

namespace P5SHighFps.Patching;

/// <summary>Applies or removes the patches while the game is suspended, and sets the frame globals accordingly.</summary>
sealed class Patcher(RemoteProcess mem, long baseAddr, int targetFps, IReadOnlyList<Patch> patches, IReadOnlyList<Patch> bisectionCandidates)
{
    readonly int origDivisor = mem.ReadInt(baseAddr + Addresses.Divisor);
    readonly int origFps = mem.ReadInt(baseAddr + Addresses.FpsInt);
    readonly int origFpsFloat = mem.ReadInt(baseAddr + Addresses.FpsFloat);

    public int OriginalDivisor => origDivisor;
    public int OriginalFps => origFps;

    /// <summary>Family switched off by the F7 diagnostics key (AlwaysOn = none).</summary>
    public Family DisabledFamily { get; set; } = Family.AlwaysOn;

    /// <summary>Indexes of the bisection candidates currently forced to 1.</summary>
    public HashSet<int> ForcedCandidates { get; set; } = [];

    public void Apply(bool on)
    {
        mem.Suspend();
        try
        {
            foreach (var p in patches)
                mem.WriteCode(p.Address, on && p.Family != DisabledFamily ? p.Replacement : p.Original);
            for (int i = 0; i < bisectionCandidates.Count; i++)
            {
                var p = bisectionCandidates[i];
                bool forced = on && DisabledFamily != Family.IntegerCounters && ForcedCandidates.Contains(i);
                mem.WriteCode(p.Address, forced ? p.Replacement : p.Original);
            }
            mem.Write(baseAddr + Addresses.Divisor, BitConverter.GetBytes(on ? 1 : origDivisor));
            mem.Write(baseAddr + Addresses.FpsInt, BitConverter.GetBytes(on ? targetFps : origFps));
            mem.Write(baseAddr + Addresses.FpsFloat, BitConverter.GetBytes(on ? BitConverter.SingleToInt32Bits(targetFps) : origFpsFloat));
        }
        finally { mem.Resume(); }
    }
}
