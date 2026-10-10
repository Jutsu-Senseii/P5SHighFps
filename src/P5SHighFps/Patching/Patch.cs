namespace P5SHighFps.Patching;

/// <summary>Families of fixes, toggled one at a time by the F7 diagnostics key.</summary>
enum Family
{
    AlwaysOn = 0,
    CheatTableSites = 1,
    RelocatedTraceSites = 2,
    SecondsPerFrame = 3,
    UiAnimations = 4,
    IntegerCounters = 5,
    ShortMotions = 6,
}

/// <summary>One code change: the original bytes (verified before patching) and the bytes that replace them.</summary>
sealed record Patch(long Address, byte[] Original, byte[] Replacement, Family Family, string Name = "");

static class FamilyNames
{
    public static string Of(Family f) => f switch
    {
        Family.AlwaysOn => "none",
        Family.CheatTableSites => "cheat table sites",
        Family.RelocatedTraceSites => "relocated .trace sites",
        Family.SecondsPerFrame => "seconds-per-frame helper",
        Family.UiAnimations => "UI animations",
        Family.IntegerCounters => "integer counters + blinking",
        Family.ShortMotions => "short looping motions",
        _ => f.ToString(),
    };

    public const int Count = 7;
}
