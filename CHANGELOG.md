# Changelog

## Unreleased

- Add complete ordered UUID-set update locks within the closed root grammar, preserving exact target and ordering validation.
- Add an offset-typed server clock expression and one-sample, whole-millisecond server expiry calculation with a representable timestamp bound.

- Reorganize documentation into matching English/Russian guides, add a local VitePress site and an optional manual GitHub Pages workflow, and consolidate agent instructions for code and documentation.
- Fix insert-ignore mapping for ordinary tables when the model has a default schema; reject actual view mappings before opening the connection.
- Admit C#'s paired methodless `short`-to-`int` conversions around a directly mapped member and short value in built-in equality predicates; retain rejection of other conversions and computed members.
- Select the expression-interceptor row-lock implementation with parameterless `UsePostgreSqlExtensions()` registration; remove the experimental carrier selector and alternate translation paths.
- Restrict lock queries to the documented root grammar and validate original expressions, mapping, final SQL scope, current transactions, service composition and cached selection bounds.
- Validate insert-ignore mappings and typed values before opening a connection; preserve selected-conflict, tracking and caller-transaction semantics.
- Add a typed single server-clock read and strengthen PostgreSQL command/provider guards.
- Add an isolated local-package consumer and CI verification. Current local preview package version `0.1.0-preview.6` is separate from NuGet publication and stable API support.
