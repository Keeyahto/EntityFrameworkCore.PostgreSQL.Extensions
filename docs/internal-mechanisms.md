# Data and transactions

This guide covers atomic insert-ignore, PostgreSQL wall-clock access and read-only transactions. These operations use the Npgsql-backed `DbContext` or its `Database` facade and can be used without `UsePostgreSqlExtensions()`. Snippets use the model from [Getting started](getting-started.md).

| Method | Result |
|---|---|
| [InsertIgnoreAsync](#insert-api) | Whether one row was inserted |
| [ReadClockTimestampAsync](#clock-api) | One server-clock value as UTC `DateTime` |
| [ReadClockExpiryAsync](#clock-expiry-api) | A shared server sample and expiry as UTC `DateTimeOffset`, or no representable expiry |
| [MapClockTimestamp / ClockTimestamp](#clock-mapping-api) | A model mapping and a server-side clock expression |
| [SetTransactionReadOnlyAsync](#read-only-api) | A read-only setting on the current transaction |

SQL uses the example model and [reference versions](compatibility.md). `$1`, `$2` are PostgreSQL parameters; values are listed beside the examples. Transaction begin/commit are omitted from SQL listings. Parameter notation and generated aliases can vary with provider versions.

<a id="insert-api"></a>
## InsertIgnoreAsync

Insert one mapped entity and ignore only a conflict on the unique key selected by the caller. This example uses `Document.Id` as that key.

**C#**

```csharp
var candidate = new Document { Id = 1, Payload = "created" };
var inserted = await db.InsertIgnoreAsync(candidate, x => x.Id, ct);
```

**SQL** (`$1 = 1`, `$2 = "created"`):

```sql
INSERT INTO "Documents" ("Id", "Payload")
VALUES ($1, $2)
ON CONFLICT ("Id") DO NOTHING;
```

The method sends one PostgreSQL `INSERT ... ON CONFLICT (...) DO NOTHING`. It returns `true` for one inserted row and `false` when the selected unique conflict prevented insertion. A conflicting row keeps its existing values. Violations of other constraints propagate as native PostgreSQL errors.

The public signature is:

```csharp
Task<bool> InsertIgnoreAsync<TEntity>(this DbContext context,
    TEntity entity, Expression<Func<TEntity, object?>> conflictTarget,
    CancellationToken cancellationToken = default)
    where TEntity : class;
```

The selector names a direct mapped property, or an anonymous composite such as `x => new { x.Code, x.Region }` for a model with those properties. Its columns must match a mapped primary key, alternate key or unfiltered unique index. Partial/expression indexes, computed selectors, duplicate members and arbitrary constructors are unsupported. PostgreSQL's native nullable-unique semantics apply: NULL does not become application-level equality.

<a id="mapping-api"></a>
### Mapping, defaults and tracking

The entity must map to one ordinary table, including a table in the model's default schema. Identifiers come from EF metadata and use provider quoting; values are parameters with relational type mappings and converters. Views, owned entities, inheritance, complex/shadow properties, table/view fragments and ambiguous shared-table mappings are rejected. Mapping, selector, SQL and parameter preparation finish before the method opens a connection.

EF's configured sentinel marks an omitted value for a generated/defaulted property. PostgreSQL supplies the omitted value. An explicit NULL can be sent only when it differs from that sentinel. A nullable property whose sentinel is NULL cannot express both omission and explicit NULL through this entity API. Unsupported generation, including explicit identity-always values and database-generated update values, is rejected. Custom defaults and converters need provider validation for their exact mapping.

`InsertIgnoreAsync` does not attach the supplied instance, change tracking state, call `SaveChanges`, run client key generators or SaveChanges interceptors, or assign/return generated values. Query separately if you need the stored entity. An already tracked entity remains governed by normal EF behavior; a later `SaveChanges` is separate work. Bulk insertion, `DO UPDATE`, general upsert and `RETURNING` are outside this API.

<a id="atomicity-api"></a>
### Transaction and atomicity

With a current attached EF transaction, the statement participates in that transaction. Without one, PostgreSQL executes it as a single autocommit statement. The helper creates no transaction and performs no check-then-insert. Concurrent inserts are resolved by PostgreSQL's unique constraint, and a competing statement can wait for an uncommitted insert.

To include insertion in a larger unit of work, create the transaction yourself:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var inserted = await db.InsertIgnoreAsync(
    new Document { Id = 1, Payload = "created" }, x => x.Id, ct);
await transaction.CommitAsync(ct);
```

<a id="clock-api"></a>
## ReadClockTimestampAsync

For one server value, use the typed database method:

**C#**

```csharp
DateTime sampledAt = await db.Database.ReadClockTimestampAsync(ct);
```

**SQL**

```sql
SELECT pg_catalog.clock_timestamp();
```

It reads `pg_catalog.clock_timestamp()` once and returns a UTC `DateTime` at PostgreSQL's microsecond precision. It borrows an attached current transaction when present and can run standalone otherwise. It balances the connection ownership acquired by the call, preserving an already-open connection. Cancellation and provider errors propagate. No client clock is substituted.

Reuse the returned value explicitly when several application values need the same sample. For a shared sample and server-calculated expiry, use the following method.

<a id="clock-expiry-api"></a>
## ReadClockExpiryAsync

`Task<(DateTimeOffset SampledAt, DateTimeOffset ExpiresAt)?> ReadClockExpiryAsync(this DatabaseFacade database, TimeSpan duration, CancellationToken cancellationToken = default)` reads one volatile server sample and computes its expiry in the same statement. The duration must be positive and contain whole milliseconds. Invalid durations throw `ArgumentOutOfRangeException` before executing a command. Whole-millisecond durations beyond `int.MaxValue` milliseconds are supported.

```csharp
var expiry = await db.Database.ReadClockExpiryAsync(TimeSpan.FromDays(30), ct);
if (expiry is { } times)
{
    DateTimeOffset sampledAt = times.SampledAt;
    DateTimeOffset expiresAt = times.ExpiresAt;
}
```

Both values have zero UTC offset and PostgreSQL microsecond precision. The calculation uses one `pg_catalog.clock_timestamp()` sample and `duration_milliseconds * interval '1 millisecond'` with a parameterized `long` duration. An expiry beyond `9999-12-31 23:59:59.999999+00` returns `null`; no expiry is clamped and no client-clock value is substituted. The upper bound is a SQL timestamp literal so provider infinity conversions cannot alter it. Application TTL limits and lease policy belong to the caller.

Interval multiplication follows PostgreSQL's native floating-point arithmetic. Very large accepted durations can round by several microseconds: `100000000000001` milliseconds produces an interval eight microseconds shorter on the reference PostgreSQL 16 combination. The method preserves that server result; it does not promise equivalence to exact .NET `TimeSpan` addition over the entire accepted duration range.

**SQL** (`$1 = 2592000000`, type `bigint`, for 30 days; no entity model is involved):

```sql
WITH sample AS MATERIALIZED (
    SELECT pg_catalog.clock_timestamp() AS sampled_at
), calculated AS (
    SELECT sampled_at,
           sampled_at + $1 * INTERVAL '1 millisecond' AS expires_at
    FROM sample
)
SELECT sampled_at,
       CASE
           WHEN expires_at > TIMESTAMPTZ '9999-12-31 23:59:59.999999+00' THEN NULL
           ELSE expires_at
       END AS expires_at
FROM calculated;
```

The method borrows an attached current transaction when present and otherwise runs as one standalone statement. It balances EF connection ownership and propagates cancellation and native provider errors. It creates no transaction, performs no write and retries nothing. Keep the returned pair together when persisting a sample and its expiry.

<a id="clock-mapping-api"></a>
## MapClockTimestamp and DatabaseClock.ClockTimestamp

`MapClockTimestamp()` returns the same `ModelBuilder` and configures both clock expressions. This registration executes no SQL. `DatabaseClock.ClockTimestamp()` represents a `DateTime` expression; `DatabaseClock.ClockTimestampOffset()` represents a `DateTimeOffset` expression. Both evaluate `pg_catalog.clock_timestamp()` on PostgreSQL inside a translated query.

Register the mapping in `OnModelCreating`:

```csharp
modelBuilder.MapClockTimestamp();
```

Then use it in a normal translated query, for example a projection over a populated table:

**C#**

```csharp
DateTime observedAt = await db.Documents
    .Select(x => DatabaseClock.ClockTimestamp())
    .FirstAsync(ct);
```

**SQL**

```sql
SELECT pg_catalog.clock_timestamp()
FROM "Documents" AS d
LIMIT 1;
```

`FirstAsync` requires a row in `Documents`; an empty table produces its ordinary no-result error. For a clock sample independent of table contents, use [ReadClockTimestampAsync](#clock-api).

`DatabaseClock.ClockTimestamp()` maps to PostgreSQL's volatile wall clock. Its value is taken when the function is evaluated. PostgreSQL `now()` instead represents transaction start time. Repeated calls can return different values inside one statement; the mapping does not promise a single shared evaluation. Direct invocation in client code throws `NotSupportedException`.

These are ordinary translated expressions without a row-lock marker; row-lock projection restrictions still apply. `ClockTimestampOffset()` can be compared with mapped `DateTimeOffset` timestamps in predicates and assigned to a timestamp or nullable timestamp through `ExecuteUpdate`. Each occurrence remains volatile, including separate occurrences in a predicate and setter. It does not make them one shared sample. Client invocation of either clock expression throws `NotSupportedException`. Arbitrary CTE/update compositions and clock arithmetic outside `ReadClockExpiryAsync` have no additional library guarantee.

<a id="read-only-api"></a>
## SetTransactionReadOnlyAsync

Mark the current explicit transaction read-only. The method returns `Task`; PostgreSQL enforces the transaction setting.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
await db.Database.SetTransactionReadOnlyAsync(ct);
var documents = await db.Documents.AsNoTracking().ToListAsync(ct);
await transaction.CommitAsync(ct);
```

**SQL command sent by the helper**

```sql
SET TRANSACTION READ ONLY;
```

The subsequent `ToListAsync` is an ordinary EF query and generates its own SELECT over `Documents`.

Call the method immediately after beginning the transaction, before queries that constrain PostgreSQL's transaction settings. It issues `SET TRANSACTION READ ONLY` under the attached current EF transaction. PostgreSQL enforces valid timing and write restrictions. The method changes no session default or isolation level and cannot repair an aborted transaction.

<a id="ownership-api"></a>
## Shared ownership and failure behavior

All operations require the Npgsql EF provider and an `NpgsqlConnection`. A borrowed transaction must be an `NpgsqlTransaction` attached to that connection. Insert-ignore and the clock read open and close through EF, preserving connection ownership. Advisory locks and the read-only command require an existing transaction.

The caller controls commit/rollback and recovery after cancellation or native errors. The library introduces no automatic retry or domain error classification. For configuration failures and probe outcomes, see [Configuration and diagnostics](capabilities.md).
