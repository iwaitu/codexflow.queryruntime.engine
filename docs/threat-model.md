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
- QRE traces under `.qre/runs`. Public traces contain redacted metadata; explicitly
  opted-in private diagnostic and sanitized-fixture traces can contain prompts,
  model output, tool arguments, tool output, commands, and policy decisions.
- Host filesystem locations outside the declared workspace.

## Actors

- Trusted local developer running QRE manually.
- Model output that may request unsafe commands.
- Repository content that may influence model/tool behavior.
- Future remote users or CI jobs, which require stronger isolation than the
  local runner provides.

## Current Controls

- Tool profiles: `none`, `readonly`, `verify`, and controlled file-edit
  `repair`.
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
- `repair` exposes controlled workspace file tools (`qre_write_file` and
  `qre_apply_patch`) instead of arbitrary shell execution. The tools reject
  workspace escape, symlink escape, protected `.git` / `.qre` artifacts, and
  exact high-confidence credential paths. Fuzzy secret-looking names are
  advisory signals, not mandatory deny rules, so ordinary files such as
  `TokenService.cs` remain usable.
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
  copy-in/copy-out. Copy-back builds a bounded change manifest, rejects deletion,
  protected/high-confidence credential paths, reparse links, device files, quota violations,
  and concurrent host edits, then applies same-directory temporary files with
  rollback backups.
- Trace readers treat JSONL, manifests, and blobs as untrusted input. They enforce
  file/line/event/depth/blob limits, workspace-to-`.qre` ancestor and run-root
  link containment, stable opened-file reads, declared length, and SHA-256
  integrity before returning blob text.
- `PublicRedacted` is the default trace data mode and is tagged `SummaryOnly`.
  It replaces host run/query identifiers with unlinkable or redacted values.
  `PrivateDiagnostic` and `SanitizedFixture` are explicit opt-ins tagged
  `FullFidelity`; only full-fidelity traces can enter recorded or strict replay.
  Private traces use an isolated directory, owner-only Windows ACLs or Unix
  `0700/0600` modes, and a bounded retention policy.

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
- Private diagnostic and sanitized-fixture traces may contain sensitive data.
  The runtime enforces local private permissions and bounded retention for private
  mode, but artifacts still must not be committed or uploaded without an explicit policy.
- Public redaction reduces accidental disclosure but is not encryption and does not
  make trace directories suitable for untrusted multi-tenant storage.
- Tools that read files can expose private repository content to a provider if
  the model client is configured for live LLM calls.

## Next Hardening Steps

- Add an approval broker for `RequireApproval` decisions.
- Add an explicit, journaled deletion mode only if a future product requirement
  needs Docker copy-back to propagate deletions.
- Keep expanding command classification through tests before exposing broader
  command execution.
