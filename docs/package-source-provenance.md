# Package Source and Provenance

QRE publishes the library package separately from the native `qre` CLI archives.
The current NuGet package ids are:

- `CodexFlow.QueryRuntime.Engine`
- `CodexFlow.QueryRuntime.Models` (MEAI/vLLM model providers; depends on `CodexFlow.QueryRuntime.Engine` of the same version)

`CodexFlow.QueryRuntime.Engine` bundles `CodexFlow.QueryRuntime.Abstractions.dll`
and `CodexFlow.QueryRuntime.Protocol.dll` inside the same package as `lib/net10.0`
assets. It should not publish or depend on separate `Abstractions` or `Protocol`
packages. `CodexFlow.QueryRuntime.Models` ships only its own assembly and gets
`Protocol` through its `Engine` dependency, so consumers never see two copies.

The package version is set by `QRE_PACKAGE_VERSION` in local builds or by the
release workflow metadata for tagged releases.

## Local Development Feed

Use a local feed for unpublished development packages. Do not allow internal QRE
package ids to resolve from public feeds when testing a downstream adapter.

Example `NuGet.config` for a local development feed:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="qre-local" value="/absolute/path/to/codexflow.queryruntime.engine/artifacts/nuget" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="qre-local">
      <package pattern="CodexFlow.QueryRuntime.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="System.*" />
      <package pattern="xunit*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Build the package and checksum metadata:

```bash
QRE_PACKAGE_VERSION=0.1.2-local.1 scripts/qre-pack-nuget.sh Release
cat artifacts/nuget/SHA256SUMS
```

Verify the local package before copying it to a downstream feed:

```bash
cd artifacts/nuget
shasum -a 256 -c SHA256SUMS
```

On Linux, `sha256sum -c SHA256SUMS` is also acceptable.

## Production Feed

Use an authorized organization feed for production consumption. Keep QRE ids
mapped only to that feed.

Example production `NuGet.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="qre-prod" value="https://pkgs.example.com/qre/nuget/v3/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="qre-prod">
      <package pattern="CodexFlow.QueryRuntime.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.*" />
      <package pattern="System.*" />
      <package pattern="VllmChatClient" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Consumers should fail the build if `CodexFlow.QueryRuntime.*` resolves from an
unauthorized source. Do not use a warning-only policy for internal package ids.

## Release Evidence

Each release should record:

- package id
- package version
- git commit SHA
- package file name
- SHA-256 digest from `SHA256SUMS`
- feed name and URL used for publication

The GitHub release workflow uploads `artifacts/nuget/SHA256SUMS` together with
the `.nupkg` and `.snupkg` files. Native CLI archives also have per-file
`.sha256` files.

## Automated NuGet Publishing

`.github/workflows/release.yml` publishes both `CodexFlow.QueryRuntime.Engine`
and `CodexFlow.QueryRuntime.Models` through NuGet Trusted Publishing. In the
`iwaitu` NuGet account, the policy must cover both package IDs and bind to:

- Repository owner: `iwaitu`
- Repository: `codexflow.queryruntime.engine`
- Workflow: `release.yml` (filename only)
- Environment: empty (the publishing job does not use a GitHub environment)

The current policy permits new versions of these existing packages. Adding a new
package ID requires a matching policy scope before attempting its first upload.
No long-lived `NUGET_API_KEY` repository secret is required: the publishing job
uses `id-token: write` and the pinned `NuGet/login` action to exchange GitHub's
OIDC token for a temporary API key immediately before uploading.

After updating `Directory.Build.props`, version examples and the CLI version
test, commit and push the changes. Open **Actions → Release → Run workflow**, set
`version` to the same version (for example `0.23.2`), and set `release_ref` to the
reviewed commit SHA on `main`. Ordinary pushes run CI only; they do not publish.

The workflow builds and tests the selected commit, validates native binaries,
checks security, packs and smoke-tests the packages, then downloads the exact
tested packages and verifies their checksums. It pushes Engine followed by
Models to `https://api.nuget.org/v3/index.json`. Only after successful NuGet
uploads does it create the GitHub Release and artifact provenance attestations.
`.snupkg` files are retained as release assets; automatic symbol publishing is
disabled.

NuGet validation and indexing may finish after the upload succeeds. Check both
package version pages before reporting public availability. If a publish attempt
partially succeeds, rerun the failed jobs; `--skip-duplicate` permits retrying
already-uploaded versions. NuGet versions remain immutable, so a changed package
requires a new version. A 401 or 403 should be investigated against the policy's
account, repository, workflow, package scope and environment, rather than worked
around by adding a permanent API key.

See [NuGet's Trusted Publishing documentation](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
