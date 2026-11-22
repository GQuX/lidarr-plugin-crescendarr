#!/usr/bin/env bash
# Runs ONE P0 crash-recovery phase in its own `dotnet test` process (issue #41, draft PR #42).
# usage: p0-crash-phase.sh <phase> <disposable-root> <pass|killed> [test-filter]
# "killed" phases must be terminated by SIGKILL at their documented crash point: a zero exit,
# a missing host-crash report or missing kill evidence fails the step.
set -uo pipefail
phase="$1"
root="$2"
expect="$3"
filter="${4:-FullyQualifiedName~p0_crash_recovery_phase}"
case "$(basename "$root")" in
  crescendarr-p0-*) ;;
  *) echo "::error::refusing non-disposable root $root"; exit 2 ;;
esac
log="$RUNNER_TEMP/$(basename "$root")-$phase.log"
CRESCENDARR_P0_PHASE="$phase" CRESCENDARR_P0_ROOT="$root" \
  dotnet test upstream-lidarr/src/NzbDrone.Core.Test/Lidarr.Core.Test.csproj \
    --configuration Debug --no-build --filter "$filter" \
    --logger 'console;verbosity=normal' -p:EnableAnalyzers=false -p:NuGetAudit=false 2>&1 | tee "$log"
rc=${PIPESTATUS[0]}
if [ "$expect" = killed ]; then
  if [ "$rc" -eq 0 ]; then echo "::error::$phase exited 0; it must be SIGKILLed mid-operation"; exit 1; fi
  if ! grep -qE 'Test host process crashed|test run was aborted' "$log"; then echo "::error::$phase failed without a test-host crash"; exit 1; fi
  if [ ! -f "$root/kill-$phase.evidence" ]; then echo "::error::$phase did not reach its kill point"; exit 1; fi
  echo "P0 $phase: test host SIGKILLed at its crash point (dotnet test rc=$rc): $(cat "$root/kill-$phase.evidence")" | tee -a "$GITHUB_STEP_SUMMARY"
else
  if [ "$rc" -ne 0 ]; then echo "::error::$phase failed (rc=$rc)"; exit "$rc"; fi
  # Console logger summary is multi-line: "Total tests: N" then "Passed: N".
  if ! grep -qE '^[[:space:]]*Passed: +[1-9]' "$log" || grep -qE '^[[:space:]]*Failed: +[1-9]' "$log"; then
    echo "::error::$phase ran no passing test"; exit 1
  fi
  echo "P0 $phase: $(grep -E '^[[:space:]]*(Total tests|Passed|Failed|Skipped):' "$log" | tr -s ' ' | tr '
' ' ')" | tee -a "$GITHUB_STEP_SUMMARY"
fi
