# Code changes

## Scope and contracts

Read the relevant user guide and [architecture](../docs/architecture.md). The guides own public behavior; source and tests establish its implementation. Resolve a disagreement before changing a guarantee. Keep changes bounded to the request and preserve unrelated work.

Keep one runtime assembly/package, `net10.0`, and the public namespace `EntityFrameworkCore.PostgreSQL.Extensions`. Prefer an existing EF Core/Npgsql API when it supplies the required semantics. Use small internal components for the mechanisms in the architecture. A new feature belongs with the relevant capability family; a new project, generic ORM/UoW, SQL dispatcher, universal AST or operation registry requires a concrete need and an explicit design decision.

## Behavior to preserve

- A marked row-lock query locks the exact validated root occurrence or fails before returning data as locked. Missing registration, unknown expressions, model transformations, cache reuse and replaced services must never silently remove locking.
- Validate original expression/model, final relational scope and command-time transaction/service/selection-bound identity. Carry immutable query state. Keep native Npgsql translation and structural SQL generation; the command interceptor never rewrites SQL text. Reject competing generator/translator replacements and manual internal service-provider composition.
- Keep the admitted query grammar in [Locking](../docs/query-integration.md). Broader query/model support requires an explicit contract change with positive and negative provider checks. Preserve native empty/multiple-result semantics and ordinary unmarked queries.
- The caller creates and completes transactions. Borrow only an EF transaction attached to the command's connection. Preserve connection ownership, cancellation, native exceptions and SQLSTATEs. Add no hidden begin/commit/retry, ambient state or session-lock fallback.
- Preserve selected-conflict insert behavior, metadata-based quoted identifiers, parameterized typed values, converters, sentinel/default rules and tracking boundaries. Keep server-side advisory hashing and the distinction between volatile wall time and transaction time.
- Keep SQL-generation target, connected server facts and installed/required extensions distinct. The probe is caller-invoked; application permissions, readiness, retry and recovery policies stay with the application.

## Verification

Use [Contributing](../CONTRIBUTING.md) for commands. Choose checks for the changed risk, and state prerequisites and anything that could not run.

For runtime behavior, add or adjust meaningful positive and negative tests. PostgreSQL locking, atomicity, mapping and transaction claims require real PostgreSQL; compilation or SQL strings alone are insufficient. Concurrency tests observe actual contention with independent sessions and bounded cleanup. Rejected-shape tests use a valid transaction, assert the intended reason and observe zero issued commands. Retain native SQL and unmarked-query controls.

For query changes, cover relevant compiled/cache/context-factory/pool reuse, service replacement and additive plugin behavior. For DML/clock/commands/probes, verify their own constraints independently. Provider-internal APIs are version-sensitive; keep EF1001 suppression local to the adapter. Dependency changes require appropriate provider and isolated packed-consumer checks for the claimed combination. A package build supplies an artifact; package execution, compatibility and publication are separate outcomes.

Before admitting a new public capability or expanding compatibility, require independent substantive review and successful relevant CI, provider and isolated-consumer checks for the same candidate. A narrower guarantee must be agreed and documented before consumers rely on it. Report unavailable, failed or incomplete checks explicitly.

## Documentation and delivery

Any change to public behavior, API, supported/rejected forms, dependencies, configuration or errors must update both language versions under `docs/` and relevant XML comments/examples in the same change. Follow [Documentation changes](documentation.md), including README and changelog updates when applicable. A change is incomplete while its affected documentation is stale. Internal-only refactoring may leave user guides unchanged when observable behavior and instructions are preserved; explain that assessment in the response.

Commit only requested deliverables. Keep transient evidence, reports and diagnostics in ignored `artifacts/` or outside the checkout. Do not create tracked acceptance reports, handoff documents, new plans or agent journals unless explicitly requested. Preserve existing technical pins and fixtures that are part of the contract. Keep private product names, governance, data, credentials and paths out of source, history, examples and package metadata. Do not stage unrelated files, publish, merge or deploy without the applicable user instruction.
