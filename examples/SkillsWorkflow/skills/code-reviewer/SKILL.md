---
name: code-reviewer
description: Expert code review skill for enforcing architecture boundaries, zero warnings, immutability, and security best practices. Use when evaluating pull requests or inspecting newly written code.
---

# Code Reviewer Skill

This skill enforces strict engineering standards and procedural quality checks.

## Core Review Principles

1. **Architecture Layering**:
   - Ensure Protocol / Domain models remain pure and free from UI/Transport concerns.
   - Verify Fail-Closed policy enforcement on all destructive or high-risk tool operations.

2. **Code Safety & Resource Management**:
   - Every `IDisposable` or `IAsyncDisposable` must have a deterministically managed lifecycle.
   - Prevent blocking calls (`.Result`, `.Wait()`) on asynchronous tasks.

3. **Compiler Zero-Warning Standard**:
   - All code must build cleanly under `<Nullable>enable</Nullable>` with 0 warnings.
