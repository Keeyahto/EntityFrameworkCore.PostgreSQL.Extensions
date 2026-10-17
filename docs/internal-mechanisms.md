# Internal mechanisms

This reference describes the current command, metadata-aware DML, clock, and compatibility mechanisms. The approved [first-production contract](briefs/production-contract.md) owns the selected package target, row-lock query contract, registration boundary, and explicit exclusions. This file records the behavior implemented by the source.

## Shared execution boundary

Command, DML, and clock operations require the Npgsql EF Core provider and an `NpgsqlConnection`. When an EF transaction is present, the operation uses it only when its underlying `NpgsqlTransaction` belongs to that same connection. These helpers create or commit no transaction and retry no command. Caller cancellation is passed to asynchronous operations and PostgreSQL/provider exceptions retain their native types and details. The capability probe has its own typed error outcomes, described in [PostgreSQL capability probe](capabilities.md).

Advisory-lock and read-only-transaction operations require an existing caller-owned EF transaction. Insert-ignore and the clock read can run with or without one. Insert-ignore and the clock read pair EF's connection open/close calls around execution; a connection already open before the call remains open after its matching close. Provider, transaction, mapping, selector, SQL, and parameter checks for insert-ignore are completed before it opens the connection.

## Transaction-scoped commands

`AdvisoryLockExtensions.AcquireTransactionAdvisoryLockAsync` exposes three key forms: text, `long`, and two `int` values. Text keys use the exact PostgreSQL expression `hashtextextended(@key, 0)` through EF interpolated execution; the other forms use PostgreSQL's native one-`bigint` and two-`integer` advisory-lock spaces. Values are parameters. Each overload validates the Npgsql provider, connection, and attached current transaction before executing SQL. The lock belongs to the caller's transaction and is released by PostgreSQL at transaction completion.

`SetTransactionReadOnlyAsync` executes `SET TRANSACTION READ ONLY` under the current attached transaction. PostgreSQL enforces its valid transaction timing and write restrictions. The helper does not change a session default or transaction isolation level. Both command families forward cancellation and preserve native PostgreSQL errors; they have no fallback or retry behavior.

## Metadata-aware insert-ignore

`InsertIgnoreExtensions.InsertIgnoreAsync<TEntity>` inserts one mapped entity row and returns `true` when one row was inserted or `false` when the selected conflict target caused `DO NOTHING`. Conflicts on other constraints and other native failures propagate. The method does not attach the entity, alter the change tracker, call `SaveChanges`, or return generated values.

The entity must map to one table through one non-owned, non-inherited CLR entity mapping. Owned entities, inheritance, complex properties, table/view fragments, views, shadow properties, and another entity sharing the table are rejected. A conflict selector can name a direct mapped scalar property or an anonymous composite containing direct mapped scalar properties. Arbitrary constructors, computed expressions, duplicate members, and non-property targets are rejected. The selected columns must match a mapped primary/alternate key or an unfiltered unique index. Partial and expression indexes are unsupported.

Mapping, selector, conflict-target, generated-value, SQL, and parameter preparation happen before connection open. Identifiers are quoted with Npgsql's identifier rules. Values are sent as parameters created by each EF relational type mapping, preserving configured store types and value converters. Database-generated sentinel values are omitted so PostgreSQL can supply defaults; explicit values that the implementation cannot safely insert, including identity-always values and database-generated update values, are rejected. EF sentinel semantics govern omission, so explicit `NULL` is sent only when it is distinguishable from the property's sentinel. Client-side value generation that requires `SaveChanges` is unsupported. There is no hidden transaction: an attached caller transaction is borrowed; otherwise PostgreSQL executes the single insert statement in native autocommit.

## PostgreSQL clock

`DatabaseClock.ClockTimestamp()` is mapped by `MapClockTimestamp()` to `pg_catalog.clock_timestamp()`. Calling the method directly in client code throws `NotSupportedException`; it has no client-clock fallback. The mapping represents PostgreSQL's wall clock at function evaluation and does not itself guarantee one evaluation across several query expressions.

`ReadClockTimestampAsync(DatabaseFacade, CancellationToken)` performs one native scalar read of `pg_catalog.clock_timestamp()`. It returns a UTC `DateTime` at PostgreSQL timestamp precision and rejects a non-UTC provider result. It borrows an attached current transaction when present and creates none otherwise. The provider and transaction are validated before connection open; the EF open/close pair preserves prior open state. Cancellation is forwarded and failures propagate after connection cleanup.

## Boundaries

These mechanisms provide no application authorization, business recovery, retry policy, host startup/readiness lifecycle, automatic SQL-target selection, extension installation, generic DML, or `RETURNING` behavior. The caller owns transaction creation, commit/rollback, and action on compatibility reports. The selected scope and exclusions are defined in the [first-production contract](briefs/production-contract.md).
