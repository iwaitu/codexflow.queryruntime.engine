# Package Source and Provenance

QRE publishes the library package separately from the native `qre` CLI archives.
The current NuGet package id is:

- `CodexFlow.QueryRuntime.Engine`

`CodexFlow.QueryRuntime.Engine` bundles `CodexFlow.QueryRuntime.Abstractions.dll`
inside the same package as a `lib/net10.0` asset. It should not publish or depend
on a separate `CodexFlow.QueryRuntime.Abstractions` package.

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

Package signing or provenance attestations should be added before publishing QRE
packages to a public feed.
