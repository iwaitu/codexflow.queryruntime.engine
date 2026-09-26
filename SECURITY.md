# Security Policy

Report vulnerabilities privately to the repository owner. Never include live credentials,
private repository contents, or private trace artifacts in a public issue.

QRE writes `PublicRedacted` traces by default. Public artifacts use an unlinkable persisted
run id and redact host `RunId`, `SessionId`, and `QueryId`. `--trace-data private` writes to
an isolated owner-only directory and applies bounded retention (seven days by default,
30 days maximum). Use `--trace-data sanitized` only for reviewed, synthetic replay fixtures.
Neither private nor sanitized mode provides encryption at rest.

`--trace-data sanitized` is a storage class for reviewed fixtures, not automatic redaction:
non-public payloads are persisted as written.

SDK outbound diagnostics (`--sdk-diagnostics`) are off by default. They persist only an
allow-listed projection (counts, roles, reviewed control parameters, package-local aliases)
in an owner-only sidecar with bounded retention, never headers, URLs, bodies, exception
messages or plaintext tool/model names; exports re-project and re-alias every record.
Known residual risk outside diagnostics: the locked model SDK copies provider error bodies
into exception messages, and the Runtime's generic `model_stream_failed` error and some CLI
output paths may carry that message. Do not treat diagnostics as proof that all program
output is redacted.

Treat every trace, manifest, checkpoint, and blob as untrusted input. Do not bypass the
bounded readers or Docker staged write-back validator when integrating QRE into a host.
