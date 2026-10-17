namespace P5SHighFps.Game;

/// <summary>
/// Addresses (RVAs relative to game.exe) and expected instruction bytes for Persona 5 Strikers, Steam v1.03
/// (TimeDateStamp 1632810770). See docs/TECHNICAL.md for how each one was found.
/// </summary>
static class Addresses
{
    public const uint ExpectedTimestamp = 1632810770;

    // Frame divisor (1 = 60 FPS, 2 = 30 FPS), frame cap (int) and frame cap (float).
    public const long Divisor = 0x2284504, FpsInt = 0x2284508, FpsFloat = 0x228450C;

    // SetFrameRate(divisor): writes the three values above.
    public const long SetFrameRate = 0x5B20B0;
    public static readonly byte[] SetFrameRateOrig = [0xB8, 0x3C, 0x00, 0x00, 0x00, 0x89, 0x0D, 0x49, 0x24, 0xCD, 0x01];

    // Divisor getter and its incremental-link thunk.
    public const long GetDivisor = 0x5B20F0, GetDivisorThunk = 0x80BBB;

    // Runs once per frame (measured): "mov eax, [divisor]" followed by "add [rdi+2ECh], eax", the engine frame counter.
    public const long FrameAnchor = 0x5B6297;
    public static readonly byte[] FrameAnchorOrig = [0x8B, 0x05, 0x67, 0xE2, 0xCC, 0x01];

    // Eye blink controller (game.exe+115100): advances round(step_seconds * 60) frames per update, i.e. always 1 at
    // 120 FPS. With integer counters on, "cvttss2si r8d, xmm0" is replaced by a read of the divisor (1, 0, 1, 0...).
    public const long BlinkFrames = 0x11511C;
    public static readonly byte[] BlinkFramesOrig = [0xF3, 0x44, 0x0F, 0x2C, 0xC0];

    // UI animation player (game.exe+B96F30, "time += step") in fixed-step mode: step = [object+38h] per update, i.e. per
    // frame (dialogue bubbles, exclamation mark...). "movss xmm1, [rdx+38h]" becomes a call that scales it by 60/target.
    public const long UiFixedStep = 0xB96F55;
    public static readonly byte[] UiFixedStepOrig = [0xF3, 0x0F, 0x10, 0x4A, 0x38];

    // Motion time advance (game.exe+1FD5D0, "time += step"): the engine already snaps the time to whole frames for some
    // unit types (branch at 1FD666, the remainder is carried to the next update). Short looping motions (2 frames or
    // less, e.g. the Persona showcase pose) break when sampled between frames: the scene is drawn misplaced every
    // other frame. "mov ecx,[rax+20h] ; cmp ecx,6Eh" becomes a jump to a stub that takes the snapping branch for them.
    public const long MotionTime = 0x1FD61E, MotionTimeSnap = 0x1FD666, MotionTimeContinue = 0x1FD624;
    public static readonly byte[] MotionTimeOrig = [0x8B, 0x48, 0x20, 0x83, 0xF9, 0x6E];

    // Direct reads of the divisor (diagnostics only): sub edx,[divisor] ; mov r11d,[divisor]
    public const long DirectRead1 = 0x5B5A2C, DirectRead2 = 0x5B64A0;

    // Code cave: int3 padding of the .arch section (65 KB of 0xCC at game.exe+8DF91). The address space around the
    // module is fully reserved by the game, so nothing can be allocated within the 2 GB a rel32 call can reach.
    public const long CodeCave = 0x96000;
    public const int CodeCaveSize = 0x400;

    // Deliberately left out, found by bisection. Replacing "cvtsi2ss" by the step is only valid when the converted value
    // is the plain divisor:
    //  - 700254, 7002CE, 704EC4, 704F3E, 7061D4, 70624E, 6FE764, 6FE7DE (cheat table) and the unpatched twins 6FAAF4 and
    //    6FAB72: gauges that move toward their target (HP/SP...) with rate = max(gap / duration, floor), then
    //    value = (int)(value + rate). The rate is computed once, while it is 0, and the value is truncated to an integer
    //    on every update: a floor below 1 (0.5, or a divisor of 0 on every other frame) leaves a rate that never moves
    //    the gauge, so the on-screen HP/SP stayed behind the real value. Their divisor reads are redirected to a getter
    //    returning 1 (FixedOneSites), the original 60 FPS behaviour: gauges animate a little faster, cosmetic only.
    //  - 2E6C20, 2E6C88: "imul eax, rate" runs before the conversion (enemy detection gauge). The constant dropped the
    //    multiplication, the gauge filled far too slowly and enemies never ambushed the player.
    //  - 6DBC9E: converts a counter (min(counter + divisor, limit)), not the divisor.
    // Unpatched, they follow the integer tick sequence (1, 0, 1, 0...), which is correct on average.

    /// <summary>
    /// "cvtsi2ss xmmN, r64" sites, grouped by destination register.
    /// Based on the "static" variant of zettozoid's cheat table; sites its author found broken are excluded
    /// (135A37, 2054E5, 75F187, 7CDC2D, 450D3C).
    /// </summary>
    public static readonly (int Reg, long[] Rvas)[] StepSites =
    [
        (0, [0x5BCD26, 0x18967C, 0x4FC39F, 0x455099, 0x419839, 0x14BE2A, 0x189448,
             0x3C2033, 0x835E0D, 0x6D70FE, 0x6D708B, 0x1FA410, 0x1AE843,
             0x6ECAF2, 0x6EE2B5, 0x6E5097, 0x6E50D1, 0x6E506B,
             // .trace section sites relocated in v1.03 (the cheat table's 2B55BBC/2B80956/3AA520F do not match)
             0x2BB351C, 0x2BE0236, 0x356C1F6, 0x19597532, 0x1967F748,
             // game.exe+20FCC0 returns divisor * game speed / 60 (seconds per frame), used by UI and portrait animations
             0x20FCCE]),
        (1, [0x6DAB16, 0x6DAB96, 0x206880, 0x1F83A1, 0x159991, 0x1EBF07, 0x419817, 0x2054AF, 0x18F823,
             0x158481, 0x1517BF, 0x1521DE, 0x158610, 0x158795, 0x147443, 0x11F08F, 0x152F94, 0x151FB1,
             0x146177,
             // sibling timers of 158481/158610/158795 missing from the cheat table; receiving 1/0 they made NPCs
             // disappear depending on the camera (found by bisection)
             0x1589DF, 0x158B47, 0x158C8B,
             // twin of 152F94 missing from the cheat table (zipline/cable slide): receiving 1/0, the character fell off
             // the cable about one time in three (found by bisection)
             0x153F5F]),
        // 6B8C4A, 7719E9 and 771E59 (UI animation speed set when the animation starts) are deliberately left out: the
        // UI fixed-step fix compensates on every update instead, which stays correct if the frame rate changes mid-way.
        (2, [0x1599B1, 0x47F254, 0x506DE4, 0x506E5C, 0x1517E6, 0x14746A,
             0x152206, 0x15206E, 0x1520D2, 0x152FBC, 0x153086, 0x151FD9, 0x14619E,
             // twin of 152FBC, same cable slide update as 153F5F
             0x153F86]),
        (3, [0x1640D9]),
        (6, [0x4F5B8B, 0x15997F, 0x186B7D]),
        (7, [0x3F89FE, 0x444ECF]),
        (8, [0x5C3AFD, 0x4F5BDA]),
        (9, [0x5C3ADF]),
    ];

    /// <summary>Step sites in the .trace section, relocated for v1.03 (their own diagnostics family).</summary>
    public static readonly long[] RelocatedTraceSites = [0x2BB351C, 0x2BE0236, 0x356C1F6, 0x19597532, 0x1967F748];

    /// <summary>The seconds-per-frame helper conversion (its own diagnostics family).</summary>
    public const long SecondsPerFrameSite = 0x20FCCE;

    /// <summary>Divisor getter calls that must never see 0 (with integer counters on they get a getter returning 1).</summary>
    public static readonly long[] FixedOneSites =
    [
        // integer divisions by the divisor (the "cmp eax,1 / jbe" calls guard the following call)
        0x16AD9E, 0x16ADA8, 0x16AE7D, 0x16AE87, 0x188B94, 0x188C2C, 0x188E6E, 0x188FF2, 0x18908D, 0x2F4F0B, 0x2F4F24,
        0x2BD65BE, 0x2BD6629,
        // float divisions by a value proportional to the divisor
        0x14C74E, 0x1B0405, 0x1FB8AE, 0x3C2025,
        // UI animation speed set once when the animation starts (divisor x speed): 0 would freeze them
        0x6B8C2E, 0x7719D3, 0x771E43,
        // loop sampling a curve from t to end with "t += divisor" (e.g. Morgana's van summon): 0 never ends the loop
        0x147CF6,
        // conversions the cheat table author excluded (enemy spawning, attack particles): original behaviour
        0x135A2D, 0x450D1B, 0x2054D6,
        // gauge floors (HP/SP...): rate = max(gap / duration, divisor), computed once while the rate is 0. A divisor of 0
        // (every other frame) leaves a tiny rate for good and the gauge never moves again (see the note on StepSites).
        // Last four: the getters of 6FE764/6FE7DE and of the twins 6FAAF4/6FAB72.
        0x70024A, 0x7002C4, 0x704EBA, 0x704F34, 0x7061CA, 0x706244, 0x6FAAEA, 0x6FAB68, 0x6FE75A, 0x6FE7D4,
    ];

    /// <summary>
    /// Diagnostics only (--diagnostics, F5/F6): every divisor getter call, forced to 1 half at a time to bisect which
    /// one misbehaves when the divisor alternates 1/0.
    /// </summary>
    public static readonly long[] BisectGetterSites =
    [
        0x11CBE4, 0x11CC14, 0x11F081, 0x135A2D, 0x137EB0, 0x143B19, 0x143B90, 0x14616A, 0x14618A, 0x147436, 0x147456,
        0x147CF6, 0x14BE1D, 0x14C661, 0x14C72B, 0x14F02D, 0x14F6AE, 0x14F6CF, 0x1517B2, 0x1517D2, 0x151FA4, 0x151FC4,
        0x152059, 0x1520BD, 0x1521D1, 0x1521F1, 0x152474, 0x152494, 0x152F87, 0x152FA7, 0x153071, 0x153F52, 0x153F72,
        0x15846F, 0x1585FE, 0x158783, 0x1589CD, 0x158B35, 0x158C79, 0x15991C, 0x159975, 0x159984, 0x15999D, 0x1640C3,
        0x16E277, 0x178423, 0x180D8D, 0x186B6C, 0x18943E, 0x189672, 0x18BD2F, 0x18F7BA, 0x18F812, 0x19597528, 0x19597573,
        0x1967F72D, 0x1969105E, 0x196939A1, 0x19694672, 0x196D4254, 0x196F8E4D, 0x1AE835, 0x1B102F, 0x1D5A41, 0x1D5A7A,
        0x1DB9D2, 0x1EBEF9, 0x1F0999, 0x1F09D8, 0x1F8394, 0x1FA05C, 0x1FA0B8, 0x1FA3F7, 0x20549D, 0x2054D6, 0x206873,
        0x20FCC4, 0x2BB3512, 0x2BE021E, 0x2E6C12, 0x2E6C7A, 0x2E8A46, 0x2E8CCA, 0x33B015, 0x33B01C, 0x33DD380, 0x356C1E2,
        0x3574704, 0x35AFE8C, 0x3672F93, 0x3672FA2, 0x3C8E8C, 0x3C8E95, 0x3CAB0E, 0x3CAB17, 0x3CBF52, 0x3CC7D2, 0x3DB928,
        0x3F8967, 0x3F899F, 0x3F89BB, 0x3F89D1, 0x3F89ED, 0x40E94D, 0x40F19F, 0x40F537, 0x41980D, 0x41982F, 0x432ECD,
        0x43CB04, 0x444EB7, 0x450D1B, 0x4517D9, 0x455087, 0x458B1B, 0x458F89, 0x45A0C7, 0x46E77E, 0x46E7EF, 0x47F243,
        0x4B7330, 0x4C58E6, 0x4C6F01, 0x4C6F12, 0x4CC2D8, 0x4CC9C9, 0x4CCBD1, 0x4D95C2, 0x4ED339, 0x4ED596, 0x4F4F83,
        0x4F4F92, 0x4F5B7A, 0x4F5BA4, 0x4FC390, 0x503C72, 0x503E75, 0x506DCF, 0x506E47, 0x519E05, 0x51BA21, 0x51D4E9,
        0x51D614, 0x51D6C6, 0x5218C5, 0x524CE6, 0x524FBB, 0x525B42, 0x554517, 0x6ADC04, 0x6B5E9D, 0x6D7076, 0x6D70E7,
        0x6DAB08, 0x6DAB88, 0x6DBC7D, 0x6DC5FD, 0x6DC74D, 0x6E5061, 0x6E508D, 0x6E50B9, 0x6E614C, 0x6E6277, 0x6E630A,
        0x6E924B, 0x6EB1E1, 0x6ECAE0, 0x6EE2A0, 0x6F0052, 0x6F4230, 0x6FAAEA, 0x6FAB68, 0x6FC86B, 0x6FE75A, 0x6FE7D4,
        0x70024A, 0x7002C4, 0x704EBA, 0x704F34, 0x7061CA, 0x706244, 0x70B3DE, 0x70C539, 0x711FF6, 0x7121DF, 0x7522E6,
        0x759B18, 0x75F173, 0x766F4E, 0x767008, 0x7CC3C4, 0x7CDC1F, 0x7CFAE9, 0x7F2CBC, 0x804533, 0x8045B2, 0x804786,
        0x8047FB, 0x804802, 0x806EFD, 0x80701F, 0x8086EE, 0x809F79, 0x80CF74, 0x80D082, 0x812FD3, 0x813428, 0x8142A9,
        0x81585E, 0x81973D, 0x81AB00, 0x823153, 0x824482, 0x824505, 0x824545, 0x835DF5, 0xBC2DA,
    ];
}
