# QRE Threat Model

Status: experimental, local-runner focused.

## Scope

QRE is a repository-local query runtime for agent loops, tool policy, traces,
replay, and sandbox execution. The current `LocalProcessSandboxRunner` is a
trusted developer-machine runner. It is useful for policy prototyping and local
verification, but it is not a hostile multi-tenant isolation boundary.

## Assets

- Workspace files and generated artifacts.
- Provider credentials inherited from the user environment.
- Git remotes and publish/deploy credentials available on the host.
- QRE traces under `.qre/runs`, including prompts, model output, tool
  arguments, tool output, and policy decisions.
- Host filesystem locations outside the declared workspace.

## Actors

- Trusted local developer running QRE manually.
- Model output that may request unsafe commands.
- Repository content that may influence model/tool behavior.
- Future remote users or CI jobs, which require stronger isolation than the
  local runner provides.

## Current Controls

- Tool profiles: `none`, `readonly`, `verify`, and declared-but-gated `repair`.
- Network policy defaults to `deny` for local sandbox jobs.
- Sandbox environments are built from an allowlist through
  `TrustedLocalSandboxEnvironment`.
- Command capability classification marks workspace writes, network access,
  package install/restore/publish, git push, Git repository writes, destructive commands, deploy
  commands, arbitrary execution, and unknown processes.
- `readonly` denies workspace-write command capabilities.
- `verify` allows build/test artifact writes only for explicitly permitted
  commands and requires approval for push, publish, install, restore,
  destructive, deploy, arbitrary execution, and network-capable commands.
- Explicit approvals are local CLI policy inputs and must include an operator
  reason. They do not override unknown command classification.
- Denied or approval-gated `qre sandbox exec` commands produce policy decision
  trace events before any process is started.
- `DockerSandboxRunner` is available as a Phase 2b-MVP isolation-capable runner.
  It defaults to Docker `--network none`, mounts only the declared workspace,
  does not mount host credential stores or the Docker socket, runs as non-root,
  drops Linux capabilities, enables `no-new-privileges`, uses a read-only root
  filesystem with tmpfs scratch space, preserves Docker's default seccomp
  profile unless a custom profile is supplied, and records the selected runner
  plus runner configuration in sandbox trace events.
- Write-capable Docker jobs use a staged copy-in/copy-out workspace by default
  instead of a direct writable bind mount. `.git` and `.qre` are excluded from
  copy-in/copy-out, and symlinks are skipped so host-side copy operations do
  not resolve links to files outside the workspace.

## Explicit Non-Goals

- The local process runner does not provide container, VM, gVisor, Kata, or
  Firecracker isolation.
- Read-only mount labels are policy metadata for the local runner; they do not
  make the host filesystem read-only.
- QRE does not yet provide an approval broker. `RequireApproval` is a blocking
  decision until an explicit approval surface is added.
- QRE must not depend on CodexFlow platform surfaces such as ASP.NET Core,
  Identity, SignalR, PostgreSQL, MongoDB, Redis, Qdrant, or the React UI.

## Residual Risks

- A wrongly classified command could execute with more authority than intended.
- Commands allowed under the local runner still execute as the local user.
- Trace files may contain sensitive data and should not be committed.
- Tools that read files can expose private repository content to a provider if
  the model client is configured for live LLM calls.

## Next Hardening Steps

- Add an approval broker for `RequireApproval` decisions.
- Tighten copy-out policy for future repair/write profiles once those profiles
  start allowing source modifications beyond build artifacts, especially around
  deletion propagation and failed-run diagnostic artifacts.
- Keep expanding command classification through tests before exposing broader
  command execution.
