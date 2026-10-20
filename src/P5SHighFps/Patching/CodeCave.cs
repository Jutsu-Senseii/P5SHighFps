using P5SHighFps.Game;
using P5SHighFps.Native;

namespace P5SHighFps.Patching;

/// <summary>
/// Layout of the code cave (int3 padding inside game.exe, see <see cref="Addresses.CodeCave"/>) and the small x86-64
/// stubs written there. Every address is within rel32 reach of the game code.
/// </summary>
/// <remarks>
/// Layout: +000 step (float) · +008 frame counter (u64) · +010 movss thunks, one per xmm register (0x10 apart) ·
/// +0B0 tick accumulator (u32) · +100 SetFrameRate replacement · +140 anchor (count only) · +180 anchor (ticks) ·
/// +1E0 "return 1" getter · +200 blink stub · +210 UI step stub · +230 constant 1 · +234 2.0f · +238 0.0f ·
/// +23C target / 60 (float) · +240 short motion stub · +2A0 gauge duration stub · +2C0 lock-on dead zone stub.
/// </remarks>
sealed class CodeCave
{
    static readonly int[] ThunkRegisters = [0, 1, 2, 3, 6, 7, 8, 9];

    readonly RemoteProcess mem;
    readonly long baseAddr;
    readonly long start;

    public long Step => start;
    public long FrameCount => start + 0x08;
    long TickAccumulator => start + 0xB0;
    public long SetFrameRateStub => start + 0x100;
    public long AnchorCountStub => start + 0x140;
    public long AnchorTicksStub => start + 0x180;
    public long ReturnOneStub => start + 0x1E0;
    public long BlinkStub => start + 0x200;
    public long UiStepStub => start + 0x210;
    public long ConstOne => start + 0x230;
    long ShortMotionMax => start + 0x234;
    long FloatZero => start + 0x238;
    long FpsRatio => start + 0x23C;
    public long ShortMotionStub => start + 0x240;
    public long GaugeStub => start + 0x2A0;
    public long LockOnStub => start + 0x2C0;
    public long Thunk(int reg) => start + 0x10 + reg * 0x10;

    public CodeCave(RemoteProcess mem, long baseAddr)
    {
        this.mem = mem;
        this.baseAddr = baseAddr;
        start = baseAddr + Addresses.CodeCave;
    }

    public bool IsEmpty() => mem.Read(start, Addresses.CodeCaveSize).All(b => b == 0xCC);

    public static byte[] Rel32(long from, long target) => BitConverter.GetBytes(checked((int)(target - from)));

    /// <summary>Writes every stub and the step value, then keeps the cave page writable for the counters.</summary>
    public void Write(int targetFps)
    {
        foreach (int reg in ThunkRegisters)
            mem.WriteCode(Thunk(reg), MovssThunk(reg));
        mem.WriteCode(FrameCount, new byte[8]);
        mem.WriteCode(AnchorCountStub, AnchorCount());
        mem.WriteCode(ReturnOneStub, [0xB8, 0x01, 0x00, 0x00, 0x00, 0xC3]); // mov eax, 1 ; ret
        mem.WriteCode(BlinkStub, BlinkFrames());
        mem.WriteCode(UiStepStub, UiStep());
        mem.WriteCode(ConstOne, BitConverter.GetBytes(1));
        mem.WriteCode(ShortMotionMax, BitConverter.GetBytes(2.0f));
        mem.WriteCode(FloatZero, BitConverter.GetBytes(0.0f));
        mem.WriteCode(ShortMotionStub, ShortMotion());
        mem.WriteCode(GaugeStub, GaugeDuration());
        mem.WriteCode(LockOnStub, LockOnDeadZone());
        Retarget(targetFps);
        // The stubs write the accumulator and counter inside the cave: its page (int3 padding only) must stay writable.
        mem.Protect(start & ~0xFFFL, 0x1000, 0x40);
    }

    /// <summary>
    /// Switches the target frame rate: the step (60 / target), the gauge ratio (target / 60), the tick stub and the
    /// SetFrameRate replacement are rewritten and the tick accumulator is reset. The game must be suspended.
    /// </summary>
    public void Retarget(int targetFps)
    {
        mem.WriteCode(Step, BitConverter.GetBytes(60f / targetFps));
        mem.WriteCode(FpsRatio, BitConverter.GetBytes(targetFps / 60f));
        mem.WriteCode(SetFrameRateStub, SetFrameRateReplacement(targetFps));
        mem.WriteCode(AnchorTicksStub, AnchorTicks(targetFps));
        mem.WriteCode(TickAccumulator, new byte[4]);
    }

    /// <summary>movss xmmN, [rip+step] ; ret</summary>
    byte[] MovssThunk(int reg)
    {
        long at = Thunk(reg);
        var t = new List<byte> { 0xF3 };
        if (reg >= 8) t.Add(0x44);
        t.AddRange([0x0F, 0x10, (byte)(((reg & 7) << 3) | 5)]);
        t.AddRange(Rel32(at + t.Count + 4, Step));
        t.Add(0xC3);
        return t.ToArray();
    }

    /// <summary>
    /// Replaces SetFrameRate: divisor = 1, cap = target (menus and cutscenes can no longer reset it).
    /// mov dword [divisor], 1 ; mov dword [fps], target ; mov dword [fps_float], target ; ret
    /// </summary>
    byte[] SetFrameRateReplacement(int targetFps)
    {
        var s = new List<byte>();
        void MovImm(long rva, int value)
        {
            long ip = SetFrameRateStub + s.Count + 10;
            s.AddRange([0xC7, 0x05]);
            s.AddRange(Rel32(ip, baseAddr + rva));
            s.AddRange(BitConverter.GetBytes(value));
        }
        MovImm(Addresses.Divisor, 1);
        MovImm(Addresses.FpsInt, targetFps);
        MovImm(Addresses.FpsFloat, BitConverter.SingleToInt32Bits(targetFps));
        s.Add(0xC3);
        return s.ToArray();
    }

    /// <summary>lock inc qword [count] ; mov eax, [divisor] ; ret</summary>
    byte[] AnchorCount()
    {
        long a = AnchorCountStub;
        var s = new List<byte>();
        s.AddRange([0xF0, 0x48, 0xFF, 0x05]); s.AddRange(Rel32(a + 8, FrameCount));
        s.AddRange([0x8B, 0x05]); s.AddRange(Rel32(a + 14, baseAddr + Addresses.Divisor));
        s.Add(0xC3);
        return s.ToArray();
    }

    /// <summary>
    /// Integer counters: once per frame, recomputes the divisor as whole 1/60 s ticks (1, 0, 1, 0... at 120 FPS).
    /// push rdx ; push rcx ; mov eax,[acc] ; add eax,60 ; xor edx,edx ; mov ecx,target ; div ecx
    /// mov [acc],edx ; mov [divisor],eax ; lock inc qword [count] ; pop rcx ; pop rdx ; ret
    /// </summary>
    byte[] AnchorTicks(int targetFps)
    {
        long a = AnchorTicksStub;
        var s = new List<byte> { 0x52, 0x51 };
        s.AddRange([0x8B, 0x05]); s.AddRange(Rel32(a + s.Count + 4, TickAccumulator));
        s.AddRange([0x83, 0xC0, 0x3C, 0x31, 0xD2, 0xB9]); s.AddRange(BitConverter.GetBytes(targetFps));
        s.AddRange([0xF7, 0xF1]);
        s.AddRange([0x89, 0x15]); s.AddRange(Rel32(a + s.Count + 4, TickAccumulator));
        s.AddRange([0x89, 0x05]); s.AddRange(Rel32(a + s.Count + 4, baseAddr + Addresses.Divisor));
        s.AddRange([0xF0, 0x48, 0xFF, 0x05]); s.AddRange(Rel32(a + s.Count + 4, FrameCount));
        s.AddRange([0x59, 0x5A, 0xC3]);
        return s.ToArray();
    }

    /// <summary>mov r8d, [divisor] ; ret</summary>
    byte[] BlinkFrames()
    {
        var s = new List<byte> { 0x44, 0x8B, 0x05 };
        s.AddRange(Rel32(BlinkStub + 7, baseAddr + Addresses.Divisor));
        s.Add(0xC3);
        return s.ToArray();
    }

    /// <summary>movss xmm1, [rdx+38h] ; mulss xmm1, [step] ; ret</summary>
    byte[] UiStep()
    {
        var s = new List<byte> { 0xF3, 0x0F, 0x10, 0x4A, 0x38, 0xF3, 0x0F, 0x59, 0x0D };
        s.AddRange(Rel32(UiStepStub + 13, Step));
        s.Add(0xC3);
        return s.ToArray();
    }

    /// <summary>
    /// Motions that take the engine's own "snap time to whole frames" branch: clips of exactly 51 or 81 frames in the
    /// pool of owner types 80 to 109 (the spark of a character switch, see <see cref="Addresses.SnapLength1"/>) and
    /// motions of 2 frames or less (the Persona showcase pose).
    /// mov ecx,[rax+20h] (owner type) ; cmp ecx,80 ; jb len2 ; cmp ecx,110 ; jae len2
    /// push rdx ; cvttss2si edx,[rbx+114h] (length) ; cmp edx,51 ; je yes ; cmp edx,81 ; je yes ; pop rdx
    /// len2: movss xmm1,[rbx+114h] ; comiss xmm1,[2.0] ; ja orig ; comiss xmm1,[0.0] ; jbe orig
    /// snap: movss xmm1,[rbx+0B8h] (time) ; jmp snap target
    /// yes: pop rdx ; jmp snap
    /// orig: cmp ecx,6Eh ; jmp continue
    /// </summary>
    byte[] ShortMotion()
    {
        long a = ShortMotionStub;
        var s = new List<byte> { 0x8B, 0x48, 0x20 };
        s.AddRange([0x83, 0xF9, (byte)Addresses.SnapOwnerTypeFrom, 0x72, 0x19]);  // jb len2
        s.AddRange([0x83, 0xF9, (byte)Addresses.SnapOwnerTypeTo, 0x73, 0x14]);    // jae len2
        s.Add(0x52);                                                              // push rdx
        s.AddRange([0xF3, 0x0F, 0x2C, 0x93, 0x14, 0x01, 0x00, 0x00]);             // cvttss2si edx,[rbx+114h]
        s.AddRange([0x83, 0xFA, (byte)Addresses.SnapLength1, 0x74, 0x2D]);        // je yes
        s.AddRange([0x83, 0xFA, (byte)Addresses.SnapLength2, 0x74, 0x28]);        // je yes
        s.Add(0x5A);                                                              // pop rdx
        if (s.Count != 33) throw new InvalidOperationException("short motion stub layout (len2)");
        s.AddRange([0xF3, 0x0F, 0x10, 0x8B, 0x14, 0x01, 0x00, 0x00]);
        s.AddRange([0x0F, 0x2F, 0x0D]); s.AddRange(Rel32(a + s.Count + 4, ShortMotionMax));
        s.AddRange([0x77, 0x19]);                                                 // ja orig
        s.AddRange([0x0F, 0x2F, 0x0D]); s.AddRange(Rel32(a + s.Count + 4, FloatZero));
        s.AddRange([0x76, 0x10]);                                                 // jbe orig
        if (s.Count != 59) throw new InvalidOperationException("short motion stub layout (snap)");
        s.AddRange([0xF3, 0x0F, 0x10, 0x8B, 0xB8, 0x00, 0x00, 0x00]);
        s.Add(0xE9); s.AddRange(Rel32(a + s.Count + 4, baseAddr + Addresses.MotionTimeSnap));
        if (s.Count != 72) throw new InvalidOperationException("short motion stub layout (yes)");
        s.AddRange([0x5A, 0xEB, 0xF0]);                                           // yes: pop rdx ; jmp snap
        if (s.Count != 75) throw new InvalidOperationException("short motion stub layout (orig)");
        s.AddRange([0x83, 0xF9, 0x6E]);
        s.Add(0xE9); s.AddRange(Rel32(a + s.Count + 4, baseAddr + Addresses.MotionTimeContinue));
        return s.ToArray();
    }

    /// <summary>cvtdq2ps xmm0, xmm0 ; mulss xmm0, [target / 60] ; divss xmm6, xmm0 ; ret</summary>
    byte[] GaugeDuration()
    {
        var s = new List<byte> { 0x0F, 0x5B, 0xC0, 0xF3, 0x0F, 0x59, 0x05 };
        s.AddRange(Rel32(GaugeStub + s.Count + 4, FpsRatio));
        s.AddRange([0xF3, 0x0F, 0x5E, 0xF0, 0xC3]);
        return s.ToArray();
    }

    /// <summary>mulss xmm2, xmm12 ; mulss xmm2, [step] ; ret</summary>
    byte[] LockOnDeadZone()
    {
        var s = new List<byte> { 0xF3, 0x41, 0x0F, 0x59, 0xD4, 0xF3, 0x0F, 0x59, 0x15 };
        s.AddRange(Rel32(LockOnStub + s.Count + 4, Step));
        s.Add(0xC3);
        return s.ToArray();
    }
}
