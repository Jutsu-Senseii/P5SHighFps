namespace P5SHighFps.Diagnostics;

/// <summary>
/// Bisection over the divisor readers (--diagnostics, F5/F6): half of the remaining suspects are forced to 1; the
/// player answers "bug gone" (F5) or "bug present" (F6), and the suspects shrink to the guilty half each time.
/// </summary>
sealed class Bisection(IReadOnlyList<string> candidateNames)
{
    List<int> suspects = Enumerable.Range(0, candidateNames.Count).ToList();
    bool started;

    /// <summary>Candidates currently forced to 1.</summary>
    public HashSet<int> Trial { get; private set; } = [];

    /// <summary>Starts the bisection on the first call, then records the answer. Returns the message to show.</summary>
    public string Answer(bool bugGone)
    {
        if (!started) { started = true; Log.Info("Bisection: start."); }
        else if (suspects.Count > 1)
        {
            Log.Info($"Bisection: {(bugGone ? "bug gone" : "bug present")} with {Trial.Count} readers forced to 1.");
            suspects = bugGone ? suspects.Where(Trial.Contains).ToList() : suspects.Where(i => !Trial.Contains(i)).ToList();
        }

        if (suspects.Count == 1)
        {
            Trial = [suspects[0]];
            return $"Culprit found: {candidateNames[suspects[0]]} (forced to 1). Check that the bug is gone.";
        }
        if (suspects.Count == 0)
        {
            Trial = [];
            return "Bisection: no suspect left (several culprits?). Everything is back to normal.";
        }
        Trial = suspects.Take(suspects.Count / 2).ToHashSet();
        return $"Bisection: {Trial.Count} of {suspects.Count} suspects forced to 1. Bug gone = F5, bug present = F6";
    }
}
