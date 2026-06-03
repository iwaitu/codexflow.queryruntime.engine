# QRE Tool Capabilities

Status: Phase 2a experimental contract.

## Tool Capabilities

Tool descriptors declare coarse runtime powers:

| Capability | Meaning |
|---|---|
| `read_fs` | Read workspace files or directory metadata. |
| `write_artifacts` | Write build/test/output artifacts in the workspace. |
| `execute_process` | Start a sandboxed local process. |
| `git_read` | Read Git status or diff data. |
| `run_tests` | Run test commands. |
| `build` | Run build commands. |

These are not sufficient by themselves for process execution. Commands also
carry command capabilities.

## Command Capabilities

`ExperimentalCommandCapabilityClassifier` assigns command-level capabilities:

| Capability | Examples |
|---|---|
| `command.read_workspace` | `rg`, `git status`, `git diff`, `dotnet build`. |
| `command.write_workspace` | shell redirects, `dotnet build`, `rm`. |
| `command.network_access` | `git push`, `npm install`, `pip install`, deploy commands. |
| `command.package_install` | `npm install`, `pnpm add`, `pip install`. |
| `command.package_restore` | `dotnet restore`, implicit restore from `dotnet build/test` without `--no-restore`. |
| `command.package_publish` | `npm publish`, `dotnet publish`. |
| `command.git_push` | `git push`. |
| `command.git_write` | `git add`, `git commit`, `git merge`, `git branch`, `git stash`. |
| `command.destructive` | `rm`, `git reset`, `git clean`. |
| `command.deploy` | `wrangler deploy`, `kubectl apply`, `terraform apply`. |
| `command.arbitrary_execution` | `dotnet run` or other commands that execute repository code. |
| `command.unknown_process` | Any process not covered by the schema. |

## Profile Rules

`none` has no tools.

`readonly` allows file tools and explicitly classified read-only process
commands such as `rg`. It denies workspace writes, shell wrappers, package
manager invocations, repository state changes, arbitrary code execution, and
unsafe command capabilities.

`verify` allows read tools, Git status/diff, `dotnet test --no-restore`, and
`dotnet build --no-restore`. It requires approval for package install/restore,
package publish, deploy, git push, Git repository writes, destructive commands,
arbitrary code execution, shell-wrapped network/install commands, and
network-capable commands.

`repair` is declared but not implemented. It returns `RequireApproval`.

## Explicit Approval

Restricted `verify` commands return `RequireApproval` by default. CLI callers
can pass `--approve-risk <reason>` to supply an explicit approval record for
policy evaluation. This converts known restricted command capabilities to
`Allow` and records `explicitApproval` plus `approvalReason` in JSON/trace
outputs.

Approval does not override unknown commands. `command.unknown_process` remains
`Deny` until the command is added to the classifier with explicit capability
coverage.

## Legacy Policy Migration

CodexFlow Core still has `CommandExecutionPolicy.VerifyWorker` during the
migration. QRE must not reference Core at runtime, but the test suite checks
that every legacy denied verify-worker subcommand is not allowed by the QRE
capability policy unless it is routed through an explicit approval decision.

This keeps the migration in dual-mode without making QRE depend on platform or
Core services.

## Trace Expectations

`qre sandbox exec` writes `policy.decision` trace events for allowed, denied,
and approval-gated commands. It also writes explicit blocked trace events:

- `policy.denied` for denied commands.
- `policy.approval_required` for known restricted commands that need approval.

A denied or approval-gated command must not start the process and therefore
must not include an `exitCode` in JSON output.

## Sandbox Runners

`qre sandbox exec` accepts `--runner local|docker`. The default is `local`.
`--runner docker` uses `DockerSandboxRunner`, defaults to `--network none`,
mounts only the requested workspace into `/workspace`, runs as non-root user
`65532:65532`, drops all Linux capabilities, enables `no-new-privileges`, uses
a read-only root filesystem with constrained `/tmp` tmpfs, preserves Docker's
default seccomp profile unless a custom profile is supplied, and records
`runner` plus Docker runner configuration in `qre sandbox exec` JSON output and
sandbox trace events. `qre run --runner docker` also includes that runner
configuration in JSON output and appends a `runner.configuration` trace event.
Write-capable Docker jobs use staged copy-in/copy-out by default rather than a
direct writable host bind mount; `.git`, `.qre`, and symlinks are not copied.
The Docker image defaults to `mcr.microsoft.com/dotnet/sdk:10.0` and can be
overridden with `--docker-image` or `QRE_DOCKER_IMAGE`.
