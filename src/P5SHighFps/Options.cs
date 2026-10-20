using System.Globalization;

namespace P5SHighFps;

/// <summary>Command line options.</summary>
sealed class Options
{
    public const string Usage =
        "Usage: P5SHighFps [--fps=120] [--auto=SECONDS] [--now] [--no-int-ticks] [--diagnostics] [path\\game.exe args...]";

    public int TargetFps { get; private set; } = 120;
    public bool Immediate { get; private set; }
    public bool IntTicks { get; private set; } = true;
    public bool Diagnostics { get; private set; }
    public int AutoDelay { get; private set; } = -1;
    /// <summary>Game command line when launched from Steam launch options ("... %command%").</summary>
    public string[]? GameCommand { get; private set; }

    /// <summary>Frame rates of the F8 and F9 keys.</summary>
    public const int PresetF8 = 120, PresetF9 = 240;

    /// <summary>Parses the arguments; returns null and an error message when they are invalid.</summary>
    public static Options? Parse(string[] args, out string? error)
    {
        var o = new Options();
        error = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--fps=")) o.TargetFps = int.Parse(a[6..], CultureInfo.InvariantCulture);
            else if (a == "--now") o.Immediate = true;
            else if (a == "--int-ticks") o.IntTicks = true; // default, accepted for compatibility
            else if (a == "--no-int-ticks") o.IntTicks = false;
            else if (a == "--diagnostics") o.Diagnostics = true;
            else if (a.StartsWith("--auto=")) o.AutoDelay = int.Parse(a[7..], CultureInfo.InvariantCulture);
            else if (!a.StartsWith("--")) { o.GameCommand = args[i..]; break; } // Steam launch options: ... %command%
            else { error = Usage; return null; }
        }
        if (o.TargetFps < 30 || o.TargetFps > 360) { error = "--fps must be between 30 and 360"; return null; }
        return o;
    }
}
