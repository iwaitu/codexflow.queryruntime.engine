# Third-Party Notices

QueryRuntime is licensed under MIT. Its NuGet dependencies retain their own licenses.
The blocking license inventory currently permits only MIT, Apache-2.0, and MPL-2.0 packages.

- Microsoft and .NET runtime/extension packages: MIT.
- xUnit test packages: Apache-2.0; they are test-only dependencies.
- `VllmChatClient`: MPL-2.0; it remains a separately distributed package dependency.

The `codex-rs` repository is used as an architectural reference. This repository does not
vendor or translate `codex-rs` source files. Any future source-level reuse must be reviewed
separately and preserve the upstream Apache-2.0 license and NOTICE obligations.

The generated SPDX SBOM attached to each release is the authoritative per-version package
inventory. Review it together with the package checksum files and this notice.
