namespace P5SHighFps.UI;

/// <summary>User feedback that works while the game has focus: on-screen banner plus beeps.</summary>
sealed class Feedback(Overlay overlay)
{
    nint gameWindow;

    public void SetGameWindow(nint handle) => gameWindow = handle;

    public void PatchState(bool on, int targetFps)
    {
        overlay.Show(on ? $"High FPS patch on: {targetFps} FPS" : "High FPS patch off: original 60 FPS",
            on ? Color.LimeGreen : Color.Gray, gameWindow);
        if (on)
        {
            Console.Beep(1000, 120);
            Console.Beep(1400, 120);
            if (targetFps >= 240) Console.Beep(1800, 120); // three beeps for the 240 FPS preset
        }
        else Console.Beep(500, 250);
    }

    public void Error()
    {
        overlay.Show("P5SHighFps: error, patch not applied", Color.OrangeRed, gameWindow);
        Console.Beep(300, 700);
    }

    public void Message(string text, Color accent) => overlay.Show(text, accent, gameWindow);
}
