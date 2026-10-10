namespace P5SHighFps;

/// <summary>Writes to the console and to P5SHighFps.log next to the exe.</summary>
static class Log
{
    static readonly StreamWriter file =
        new(Path.Combine(AppContext.BaseDirectory, "P5SHighFps.log"), append: false) { AutoFlush = true };

    public static void Info(string s)
    {
        Console.WriteLine(s);
        FileOnly(s);
    }

    /// <summary>Periodic entries that would flood the console.</summary>
    public static void FileOnly(string s) => file.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {s}");
}
