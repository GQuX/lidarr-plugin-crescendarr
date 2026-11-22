namespace Crescendarr.RecoveryLab;

/// <summary>Simulated process death inside the lab: nothing after the throw point runs.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException(string point) : base($"SIMULATED CRASH at {point}") => Point = point;

    public SimulatedCrashException() { Point = string.Empty; }

    public SimulatedCrashException(string message, Exception inner) : base(message, inner) { Point = string.Empty; }

    public string Point { get; }
}

/// <summary>
/// Named fault points. Exactly one may be armed per process:
/// <list type="bullet">
/// <item><c>crash</c>: throw <see cref="SimulatedCrashException"/> (in-process crash simulation);</item>
/// <item><c>fail</c>: throw <see cref="IOException"/> (failed fsync/rename/transfer);</item>
/// <item><c>pause</c>: print <c>CHECKPOINT name</c> and block forever so a parent can SIGKILL the process.</item>
/// </list>
/// Environment: CRESCENDARR_LAB_FAULT=mode:point.
/// </summary>
public static class Faults
{
    public const string Variable = "CRESCENDARR_LAB_FAULT";
    private static string? _mode;
    private static string? _point;
    private static readonly List<string> Visited = new();

    public static IReadOnlyList<string> VisitedPoints => Visited;

    /// <summary>
    /// In-process specs only: runs at a named point to simulate a concurrent change (e.g. another
    /// process editing a file between the executor's last check and its destructive call).
    /// </summary>
    public static (string Point, Action Action)? Interleave { get; set; }

    public static void Arm(string? mode, string? point)
    {
        if (mode is not (null or "crash" or "fail" or "pause")) throw new ArgumentException("mode must be crash, fail or pause", nameof(mode));
        (_mode, _point) = (mode, point);
        Visited.Clear();
    }

    public static void ArmFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(value)) { Arm(null, null); return; }
        var parts = value.Split(':', 2);
        if (parts.Length != 2) throw new ArgumentException($"{Variable} must be mode:point");
        Arm(parts[0], parts[1]);
    }

    public static void Hit(string point)
    {
        Visited.Add(point);
        if (Interleave is { } interleave && string.Equals(interleave.Point, point, StringComparison.Ordinal))
        {
            Interleave = null;
            interleave.Action();
        }

        if (_point is null || !string.Equals(point, _point, StringComparison.Ordinal)) return;
        switch (_mode)
        {
            case "crash":
                throw new SimulatedCrashException(point);
            case "fail":
                throw new IOException($"SIMULATED I/O FAILURE at {point}");
            case "pause":
                Console.Out.WriteLine($"CHECKPOINT {point}");
                Console.Out.Flush();
                Thread.Sleep(Timeout.Infinite);
                break;
        }
    }
}
