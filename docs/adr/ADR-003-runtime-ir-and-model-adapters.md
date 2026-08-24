# ADR-003: Provider-free Runtime IR and model adapters

- Status: Accepted
- Date: 2026-08-24
- Owners: Runtime maintainers

## Context

The v1 Engine and Abstractions expose Microsoft.Extensions.AI types. This couples core execution semantics to an
adapter representation and makes malformed or provider-specific stream behavior difficult to classify.

## Decision

Create one minimal `CodexFlow.QueryRuntime.Protocol` assembly. It contains typed IDs, Runtime items, model
request/stream events, tool call/result contracts, usage/warnings and typed termination/error values. It has no
MEAI, VLLM, CLI or sandbox implementation dependency.

MEAI conversion lives in an adapter outside Protocol. Provider selection and OpenAI-compatible details stay in
the Models layer. Unsupported provider items become explicit protocol events or typed errors, never silently
enter the core.

## Consequences

- C1 adds architecture tests for dependency direction.
- v1 contracts remain available through the compatibility window.
- Initial IR supports text, reasoning separation, tool calls/results and artifact references; multimodal expansion
  is additive and demand-driven.
