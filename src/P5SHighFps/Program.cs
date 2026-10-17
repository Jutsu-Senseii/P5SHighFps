// P5SHighFps - external high frame rate patcher for Persona 5 Strikers (Steam v1.03, game.exe TimeDateStamp 1632810770).
//
// How the engine keeps time:
//  - A frame divisor is stored at game.exe+2284504 (1 = 60 FPS, 2 = 30 FPS); the frame cap at +2284508 (int) and
//    +228450C (float). SetFrameRate (game.exe+5B20B0) writes all three.
//  - Movement/animation code reads the divisor and converts it to float (cvtsi2ss xmmN, r64) to get the simulation
//    step in 1/60 s ticks; integer counters add it directly ("counter += divisor").
//
// What this patcher does (in memory only, nothing is written to disk):
//  1. Float step sites (based on zettozoid's "Static FPS Cap Changer" cheat table for v1.02, extended for v1.03):
//     each cvtsi2ss is replaced by a call to a thunk loading 60/target (0.5 at 120 FPS); the cap is forced to target.
//  2. Integer counters (on by default, --no-int-ticks to disable): a code point that runs exactly once per frame
//     rewrites the divisor as whole 1/60 s ticks (1, 0, 1, 0... at 120 FPS), so "counter += divisor" keeps real-time
//     speed. Callers that must never see 0 (divisions, one-time speed setups) get a getter that always returns 1.
//  3. Specific fixes found in game: eye blinking, UI animations with a fixed per-update step, a seconds-per-frame helper,
//     short looping motions (Persona showcase) snapped to whole frames.
// Every site is checked (expected instruction bytes) before being patched; a site that does not match is skipped.

using System.Diagnostics;
using System.Globalization;
using P5SHighFps;
using P5SHighFps.Diagnostics;
using P5SHighFps.Game;
using P5SHighFps.Native;
using P5SHighFps.Patching;
using P5SHighFps.UI;

const string Version = "1.0.1";

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    Console.WriteLine($"Unexpected error: {e.ExceptionObject}");
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
};

var options = Options.Parse(args, out var optionsError);
if (options == null) { Console.WriteLine(optionsError); return 1; }

var feedback = new Feedback(new Overlay());
int Fail(int code, string msg)
{
    Log.Info(msg);
    feedback.Error();
    Console.WriteLine("Press Enter to close.");
    Console.ReadLine();
    return code;
}

Log.Info($"P5SHighFps {Version}: target {options.TargetFps} FPS, simulation step " +
    $"{options.Step.ToString(CultureInfo.InvariantCulture)} tick/frame, integer counters {(options.IntTicks ? "on" : "off")}" +
    $"{(options.Diagnostics ? ", diagnostics on" : "")}.");

// 1. Game process and version
if (options.GameCommand != null) GameLauncher.Start(options.GameCommand);
var game = GameLauncher.WaitForGame();
if (game == null) return 0;
feedback.SetGameWindow(game.MainWindowHandle);
using var mem = new RemoteProcess(game);
long baseAddr = (long)game.MainModule!.BaseAddress;
if (!GameLauncher.IsSupportedVersion(mem, game, baseAddr))
    return Fail(2, $"ERROR: unsupported game version (expected TimeDateStamp {Addresses.ExpectedTimestamp}, Steam v1.03). No patch applied.");

// 2. Activation: automatic after a delay (Steam launch options), immediate, or on F8
if (options.AutoDelay >= 0 && !options.Immediate)
{
    Log.Info($"Activating automatically in {options.AutoDelay} s (press F8 to activate now).");
    if (!Keyboard.WaitOrF8(game, options.AutoDelay)) return 0;
}
else if (!options.Immediate)
{
    Log.Info("Press F8 in game (title screen or later) to activate the patch.");
    if (Keyboard.WaitKey(game, [Keyboard.F8]) == 0) return 0;
}

// 3. Checks, then stubs in the code cave
if (!mem.Read(baseAddr + Addresses.SetFrameRate, Addresses.SetFrameRateOrig.Length).SequenceEqual(Addresses.SetFrameRateOrig))
    return Fail(3, "ERROR: SetFrameRate does not match the expected bytes. No patch applied.");
var cave = new CodeCave(mem, baseAddr);
if (!cave.IsEmpty())
    return Fail(4, "ERROR: the code cave is not empty (already patched? restart the game). No patch applied.");
cave.Write(options.TargetFps, options.Step);

// 4. Patch list (everything is verified before anything is written to game code)
var builder = new PatchBuilder(mem, baseAddr, cave);
builder.AddStepSites();
builder.AddUiFixedStep();
builder.AddShortMotions();
if (!builder.AddFrameAnchor(options.IntTicks))
    return Fail(5, "ERROR: the per-frame anchor does not match the expected bytes. No patch applied.");
if (options.IntTicks)
{
    builder.AddIntegerCounters();
    if (options.Diagnostics) builder.AddBisectionCandidates();
}

var patcher = new Patcher(mem, baseAddr, options.TargetFps, builder.Patches, builder.BisectionCandidates);
Log.Info($"Game state before patching: divisor {patcher.OriginalDivisor}, cap {patcher.OriginalFps} FPS");
if (patcher.OriginalDivisor != 1)
    Log.Info("WARNING: the game is set to 30 FPS; set it to 60 FPS in the options for consistent results.");

// 5. Sanity check: runs per second of the per-frame anchor, logged every 5 s (should match the frame rate in game)
bool enabled = true;
var rateWatch = Stopwatch.StartNew();
long lastCount = 0;
void LogFrameRate()
{
    if (rateWatch.ElapsedMilliseconds < 5000 || game.HasExited) return;
    long count;
    try { count = BitConverter.ToInt64(mem.Read(cave.FrameCount, 8)); } catch { return; }
    double rate = (count - lastCount) * 1000.0 / rateWatch.ElapsedMilliseconds;
    lastCount = count;
    rateWatch.Restart();
    Log.FileOnly($"frames: {rate:F1}/s{(enabled ? "" : " (patch off)")}");
}

// 6. Apply, then react to keys until the game exits
patcher.Apply(true);
feedback.PatchState(true, options.TargetFps);
Log.Info($"Patch ON ({options.TargetFps} FPS). F8 toggles on/off. Two high beeps = on, one low beep = off.");
Log.Info("Closing this window leaves the game as it is; restarting the game undoes everything.");

var bisection = new Bisection(builder.BisectionCandidates.Select(p => p.Name).ToList());
int[] keys = options.Diagnostics ? [Keyboard.F8, Keyboard.F7, Keyboard.F5, Keyboard.F6] : [Keyboard.F8];
int key;
while ((key = Keyboard.WaitKey(game, keys, LogFrameRate)) != 0)
{
    if ((key == Keyboard.F5 || key == Keyboard.F6) && enabled && builder.BisectionCandidates.Count > 0)
    {
        string msg = bisection.Answer(bugGone: key == Keyboard.F5);
        patcher.ForcedCandidates = bisection.Trial;
        patcher.Apply(true);
        feedback.Message(msg, Color.DeepSkyBlue);
        Console.Beep(key == Keyboard.F5 ? 1300 : 700, 120);
        Log.Info(msg);
        continue;
    }
    if (key == Keyboard.F7)
    {
        if (!enabled) continue;
        patcher.DisabledFamily = (Family)(((int)patcher.DisabledFamily + 1) % FamilyNames.Count);
        patcher.Apply(true);
        var off = patcher.DisabledFamily;
        string msg = off == Family.AlwaysOn ? "Diagnostics: all fixes on" : $"Diagnostics: family {(int)off} off ({FamilyNames.Of(off)})";
        feedback.Message(msg, off == Family.AlwaysOn ? Color.LimeGreen : Color.Gold);
        for (int b = 0; b < Math.Max(1, (int)off); b++) Console.Beep(800, 90);
        Log.Info(msg);
        continue;
    }
    enabled = !enabled;
    patcher.Apply(enabled);
    feedback.PatchState(enabled, options.TargetFps);
    Log.Info(enabled ? $"Patch ON ({options.TargetFps} FPS)" : "Patch OFF (original behaviour, 60 FPS)");
}
return 0;
