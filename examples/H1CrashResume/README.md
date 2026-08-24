# H1 crash-resume harness

This test-only example exits its own process with `Environment.FailFast` immediately
after a durable `StepPrepared` checkpoint is written. It is used to verify that a
separate `qre` process, including a Native AOT binary, can recover the unfinished
same-version Turn:

```bash
dotnet run --project examples/H1CrashResume -- /tmp/qre-h1-crash
qre resume latest --workspace /tmp/qre-h1-crash \
  --response "H1_RESUME_OK" --json
```

The first command is expected to exit abnormally. Use only a disposable workspace.
Pass `api-url model api-mode` after the workspace when the recovery command will use
a real provider; H1 binds those values into the host compatibility fingerprint.
