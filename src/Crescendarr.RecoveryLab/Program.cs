using System.Text.Json;
using Crescendarr.RecoveryLab;

// Disposable local recovery lab CLI. Requires CRESCENDARR_RECOVERY_LAB=DISPOSABLE_LOCAL_ONLY and
// a lab root under the system temp directory. Never touches Lidarr, a NAS, SMB or a downloader.
//   init <labRoot>                 create a new synthetic lab
//   run <labRoot>                  start or resume the operation (automatic actions only)
//   recover <labRoot> [--approve]  resume; --approve executes ONE operator-approval action
//   reconcile <labRoot>            print the reconciliation verdict as JSON
// Fault injection: CRESCENDARR_LAB_FAULT=crash|fail|pause:<point>
try
{
    Faults.ArmFromEnvironment();
    if (args.Length < 2) { Console.Error.WriteLine("usage: init|run|recover|reconcile <labRoot> [--approve]"); return 2; }
    var lab = args[1];
    switch (args[0])
    {
        case "init":
            Console.WriteLine(LabScenario.Create(lab));
            return 0;
        case "run":
            Print(Lab.Run(lab));
            return 0;
        case "recover":
            Print(Lab.Recover(lab, args.Contains("--approve")));
            return 0;
        case "reconcile":
            Console.WriteLine("RECONCILIATION " + Lab.Describe(Lab.Reconcile(lab)));
            return 0;
        default:
            Console.Error.WriteLine("unknown command");
            return 2;
    }
}
catch (LabSafetyException error)
{
    Console.Error.WriteLine("LAB_SAFETY_REFUSED " + error.Message);
    return 3;
}
catch (SimulatedCrashException error)
{
    Console.Error.WriteLine(error.Message);
    return 4;
}

static void Print(ExecutorReport report) => Console.WriteLine("REPORT " + JsonSerializer.Serialize(new
{
    verdict = report.FinalVerdict.ToString(),
    status = report.Status.ToString(),
    executed = report.Executed,
    pending = report.Pending is null ? null : new { action = report.Pending.Action.ToString(), mode = report.Pending.Mode.ToString(), trackId = report.Pending.TrackId },
    blockers = report.Blockers,
}));
