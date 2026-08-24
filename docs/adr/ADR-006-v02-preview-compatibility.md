# ADR-006: 0.2 preview compatibility and rollback

- Status: Superseded by ADR-007
- Date: 2026-08-24
- Owners: Runtime maintainers

## Decision

Route two ships as `0.2.0-preview.*`. Source-breaking v2 APIs are allowed with a migration guide, but v1 `0.1.2`
contracts remain available during the preview window. We do not promise no-recompile binary replacement before
1.0 or before unknown external consumers require it.

CLI/CodexFlow adopt v2 behind a feature flag. Existing v1 trace readers remain supported and unknown v2 schema
versions fail explicitly. In-flight Turns never move between backends; rollback starts a new request on v1.

## Consequences

- No `0.1.2` artifact is republished or mutated.
- Preview comparison is strict for policy, tool order, terminal reason and side-effect count; final text has a
  separately documented tolerance.
- Removing the v1 loop requires the Core Parity Gate, migration guide and preview observation window.
