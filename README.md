# CodexFlow QueryRuntime

Standalone repository for the CodexFlow QueryRuntime suite split from the original `codexflow` repository.

## Projects

Runtime projects:

- `CodexFlow.QueryRuntime.Engine`
- `CodexFlow.QueryRuntime.Abstractions`
- `CodexFlow.QueryRuntime.Cli`
- `CodexFlow.QueryRuntime.Experimental`
- `CodexFlow.QueryRuntime.Sandbox.LocalProcess`
- `CodexFlow.QueryRuntime.Sandbox.Docker`

Test projects:

- `CodexFlow.QueryRuntime.UnitTests`
- `CodexFlow.QueryRuntime.IntegrationTests`

This repository intentionally does not include `CodexFlow.Core`. Core-side bridge
coverage belongs in the original CodexFlow repository, with Core consuming
QueryRuntime through adapters.

## Build

```bash
dotnet build CodexFlow.QueryRuntime.slnx
```

## Test

```bash
dotnet test CodexFlow.QueryRuntime.UnitTests/CodexFlow.QueryRuntime.UnitTests.csproj
```

Docker sandbox integration tests are gated:

```bash
RUN_QUERY_RUNTIME_DOCKER_TESTS=true dotnet test CodexFlow.QueryRuntime.IntegrationTests/CodexFlow.QueryRuntime.IntegrationTests.csproj \
  --filter "FullyQualifiedName~DockerSandboxRunnerIntegrationTests"
```

## CLI

```bash
dotnet run --project CodexFlow.QueryRuntime.Cli -- --version
```

Native AOT publish smoke:

```bash
dotnet publish CodexFlow.QueryRuntime.Cli -c Release -r osx-arm64 -p:PublishAot=true -p:SelfContained=true
```

## Documentation

QueryRuntime design and phase planning docs live under `docs/`.

Primary QueryRuntime docs:

- `docs/queryruntime-technical-guide.md`
- `docs/queryruntime-harness-open-source-strategy.md`
- `docs/queryruntime-next-development-plan.md`
- `docs/queryruntime-next-development-plan.zh-CN.md`
- `docs/queryruntime-tool-partition-matrix.md`

Related architecture and runtime docs:

- `docs/adr/`
- `docs/kernel-tech.md`
- `docs/runtime-stop-hooks-tech.md`
- `docs/tool-capabilities.md`
- `docs/threat-model.md`

Historical planning, reviews, and regressions:

- `docs/archived-blueprints/`
- `docs/bugfixed/`
- `docs/review-gates/`
- `docs/review/`
- `docs/spike-reports/`
- `docs/spike-data/`
