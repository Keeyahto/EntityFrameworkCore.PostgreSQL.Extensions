# Changelog

## Unreleased

- Select the expression-interceptor row-lock implementation with parameterless `UsePostgreSqlExtensions()` registration; remove the experimental carrier selector and alternate translation paths.
- Restrict lock queries to the documented root grammar and validate original expressions, mapping, final SQL scope, current transactions, service composition and cached selection bounds.
- Validate insert-ignore mappings and typed values before opening a connection; preserve selected-conflict, tracking and caller-transaction semantics.
- Add a typed single server-clock read and strengthen PostgreSQL command/provider guards.
- Add an isolated local-package consumer and CI verification. Local preview version `0.1.0-preview.2` is separate from NuGet publication and stable API support.
