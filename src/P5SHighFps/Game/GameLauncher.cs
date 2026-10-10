using System.Diagnostics;
using P5SHighFps.Native;

namespace P5SHighFps.Game;

/// <summary>Starts the game (Steam launch options), waits for its process and checks the game version.</summary>
static class GameLauncher
{
    /// <summary>Starts the game command line received from Steam ("... %command%").</summary>
    public static void Start(string[] command)
    {
        var psi = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(command[0]))!,
        };
        foreach (var a in command[1..]) psi.ArgumentList.Add(a);
        Log.Info($"Starting the game: {command[0]}");
        Process.Start(psi);
    }

    /// <summary>Waits for game.exe and its window. Returns null if the game exits first.</summary>
    public static Process? WaitForGame()
    {
        Log.Info("Waiting for game.exe ...");
        Process? proc = null;
        while (proc == null)
        {
            proc = Process.GetProcessesByName("game").FirstOrDefault();
            if (proc == null) Thread.Sleep(1000);
        }
        // Give the game time to load and create its window before reading its memory.
        while (!proc.HasExited && proc.MainWindowHandle == 0) { Thread.Sleep(500); proc.Refresh(); }
        if (proc.HasExited) { Log.Info("The game has exited."); return null; }
        return proc;
    }

    /// <summary>Reads the PE header of the running game.exe and checks its TimeDateStamp.</summary>
    public static bool IsSupportedVersion(RemoteProcess mem, Process proc, long baseAddr)
    {
        int peOff = mem.ReadInt(baseAddr + 0x3C);
        uint ts = (uint)mem.ReadInt(baseAddr + peOff + 8);
        int imageSize = mem.ReadInt(baseAddr + peOff + 24 + 56);
        Log.Info($"game.exe PID {proc.Id}, base 0x{baseAddr:X}, TimeDateStamp {ts}, SizeOfImage 0x{imageSize:X}");
        return ts == Addresses.ExpectedTimestamp;
    }
}
