# H1 Crash-Resume Harness

This test harness simulates an unexpected process crash via `Environment.FailFast` immediately after a durable `StepPrepared` checkpoint is persisted to disk.

It demonstrates how QRE's **H1 Local Crash Recovery** mechanism enables a separate process (including a Native AOT `qre` binary) to resume an interrupted Turn without losing state.

---

## How It Works

1. **Crash Injection**: `H1CrashResume` creates a new Turn attempt, writes an atomic `StepPrepared` checkpoint to `.qre/v2/runs/h1-crash-source/checkpoint.v1.json`, and immediately invokes `Environment.FailFast()`.
2. **Atomic Recovery**: A subsequent `qre resume latest` command inspects the checkpoint, validates the **Attempt Lease**, verifies the **Recovery Compatibility ID** (matching workspace, policy, model, and tool catalog hashes), and resumes execution as a new `RunAttempt`.

---

## Step-by-Step Walkthrough

### 1. Crash the Process

```bash
# macOS / Linux
dotnet run --project examples/H1CrashResume -- /tmp/qre-h1-crash

# Windows PowerShell
dotnet run --project examples/H1CrashResume -- ./temp_h1_crash
```

*(Note: The command is intentionally designed to abort abnormally with an exit code of 1 / FailFast).*

### 2. Resume the Unfinished Turn

Resume using an offline deterministic model response:

```bash
# macOS / Linux
qre resume latest --workspace /tmp/qre-h1-crash --response "H1_RESUME_OK" --json

# Windows PowerShell
qre resume latest --workspace ./temp_h1_crash --response "H1_RESUME_OK" --json
```

Output:
```json
{
  "type": "qre.v2.resume.completed",
  "finalText": "H1_RESUME_OK",
  "status": "Completed",
  "parentAttemptId": "attempt-h1-crash-source",
  "attemptOrdinal": 1
}
```

---

## Safety Guarantees

- **Drift Prevention**: If the workspace, tool profile, model, or tool catalog hashes differ from the original checkpoint, QRE refuses to resume (fail-closed).
- **Lease Ownership**: Prevents concurrent duplicate recovery attempts.
- **Audit Continuation**: The resumed run records lineage linking back to the `rootAttemptId` and `parentAttemptId`.
