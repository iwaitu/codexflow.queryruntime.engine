# H1 Local Crash Resume Threat Model

## Trust boundary

H1 trusts the current local process and the explicitly selected workspace. A
checkpoint file is untrusted input even when it is below `.qre`: another local
process, restore operation, or user can replace it. Model output, tool arguments,
history, paths, manifests, and checkpoint metadata are also untrusted.

## Protected properties

- No provider call or tool execution before checkpoint integrity, schema,
  identity, lineage, request fingerprint, workspace, and stable-boundary checks.
- No automatic execution when a tool outcome may be unknown.
- No overwrite of prior-attempt audit or checkpoint evidence.
- No checkpoint path escape below the trusted canonical run directory, allocation beyond the configured read quota,
  or silent downgrade to public-redacted data.
- No claim of cross-version or multi-owner safety.

## Threats and controls

| Threat | Control | Required test |
| --- | --- | --- |
| Truncated or edited checkpoint | Envelope length plus SHA-256; strict JSON and schema validation | byte flip, truncation, trailing data |
| Oversized payload or JSON bomb | bounded file/payload/depth limits before deserialization | file and declared-length limits |
| Path escape or link substitution | CLI validates the canonical run directory under the workspace; checkpoint-path links inside that trusted run directory are rejected before and after opening; one bounded file handle supplies the snapshot | traversal and reparse fixture |
| Resume a different request/workspace | canonical frozen-request fingerprint and exact identity comparison | policy, budget, tool schema, workspace mismatch |
| Resume with the same attempt | parent/root/ordinal validation and new attempt requirement | duplicate and broken lineage |
| Crash after tool started but before observation durability | model-with-tools checkpoint is `NeedsReconciliation` | zero provider calls and zero tool calls |
| Replay already committed text response | rehydrate and commit the saved response without sampling | provider call count remains zero |
| Secret exposure through public trace | no persistent checkpoint sink in public-redacted mode | public run has no checkpoint material |
| Cross-version misread | exact Runtime contract version check | incompatible version rejected |
| Terminal replay | terminal checkpoint rejected as already complete | no execution |

## Crash-window matrix

| Last durable checkpoint | Resume action |
| --- | --- |
| TurnStarted | start Step 0 |
| StepPrepared | discard the incomplete in-memory Step and sample that Step again |
| ModelCommitted, no tool calls | commit saved assistant output; run termination decision |
| ModelCommitted, one or more tool calls | stop as `NeedsReconciliation` |
| ToolBatchCommitted | continue with the next Step; never repeat the committed tools |
| StepCommitted | run termination decision; do not sample again |
| Terminal | return already-terminal classification; do not resume |

## Residual risk

The StepPrepared window can repeat a provider request and its cost. Host
termination policies can be called again after a StepCommitted checkpoint and
therefore must remain side-effect-free. H1 does not resolve uncertain external
tool outcomes; H3 is required before any narrower automatic recovery promise.
An attacker that can write as the same OS account can also replace a checkpoint
and recompute its unkeyed digest; H1 provides corruption detection and path/
resource safety, not source authentication against that account.
Rejecting a linked ancestor above the already selected run directory and binding an opened handle to a physical file
identity are defense-in-depth work, not guarantees made by H1.
Full ancestor-chain proof for arbitrary orphan attempt documents, Docker image/executable content identity, and
external executable content identity are also outside H1. H1 binds the supported single-owner lineage shape and the
effective configuration strings/manifest fields; H2 or host deployment policy must provide stronger ownership and
binary provenance guarantees when required.
