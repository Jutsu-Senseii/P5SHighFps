using System.Diagnostics;
using System.Runtime.InteropServices;

namespace P5SHighFps.UI;

/// <summary>
/// Global key polling. The game freezes when it loses focus, so keys are read globally (while the game window is
/// active) instead of from the console.
/// </summary>
static class Keyboard
{
    public const int F5 = 0x74, F6 = 0x75, F7 = 0x76, F8 = 0x77, F9 = 0x78;

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Waits until one of the keys is pressed. Returns its code, or 0 if the game exits first.
    /// <paramref name="periodic"/> runs every ~30 ms while waiting.
    /// </summary>
    public static int WaitKey(Process game, int[] keys, Action? periodic = null)
    {
        var was = keys.Select(IsDown).ToArray();
        while (true)
        {
            Thread.Sleep(30);
            periodic?.Invoke();
            if (game.HasExited) { Log.Info("The game has exited."); return 0; }
            for (int i = 0; i < keys.Length; i++)
            {
                bool d = IsDown(keys[i]);
                if (d && !was[i]) return keys[i];
                was[i] = d;
            }
        }
    }

    /// <summary>
    /// Waits up to <paramref name="seconds"/>, or less if one of the keys is pressed. Returns the key code, -1 if the
    /// time ran out, or 0 if the game exits first.
    /// </summary>
    public static int WaitKeyOrTimeout(Process game, int[] keys, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var was = keys.Select(IsDown).ToArray();
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(30);
            if (game.HasExited) { Log.Info("The game has exited."); return 0; }
            for (int i = 0; i < keys.Length; i++)
            {
                bool d = IsDown(keys[i]);
                if (d && !was[i]) return keys[i];
                was[i] = d;
            }
        }
        return -1;
    }

    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
}
