# Security Policy

Report vulnerabilities privately to the repository owner. Never include live credentials,
private repository contents, or private trace artifacts in a public issue.

QRE writes `PublicRedacted` traces by default. Public artifacts use an unlinkable persisted
run id and redact host `RunId`, `SessionId`, and `QueryId`. `--trace-data private` writes to
an isolated owner-only directory and applies bounded retention (seven days by default,
30 days maximum). Use `--trace-data sanitized` only for reviewed, synthetic replay fixtures.
Neither private nor sanitized mode provides encryption at rest.

Treat every trace, manifest, checkpoint, and blob as untrusted input. Do not bypass the
bounded readers or Docker staged write-back validator when integrating QRE into a host.
