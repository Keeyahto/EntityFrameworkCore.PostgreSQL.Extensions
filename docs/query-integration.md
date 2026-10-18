# Locking

Use a row lock to protect existing mapped rows while your transaction reads and changes them. Use a transaction-scoped advisory lock to coordinate cooperating operations by a key, including work for which no row exists yet. Both forms require the caller's current explicit EF transaction and are released by PostgreSQL when that transaction completes.

Examples use the model from [Getting started](getting-started.md). Register row-lock support as shown in [Configuration](capabilities.md#registration-api); advisory locks need only the Npgsql provider.

<a id="row-locks-api"></a>
## Row locks

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var document = await db.Documents
    .Where(x => x.Id == 1)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);
document.Payload = "changed";
await db.SaveChangesAsync(ct);
await transaction.CommitAsync(ct);
```

This requests `FOR UPDATE OF` the generated root alias. If another transaction holds an incompatible lock, `NoWait` raises PostgreSQL's lock-not-available error. A query with a lock marker either obtains the requested lock on the validated root occurrence or fails explicitly. It cannot successfully return data as locked after silently dropping the request.

<a id="modes-api"></a>
### Strength and wait mode

| API or value | PostgreSQL behavior |
|---|---|
| `ForUpdate()` / `ForUpdate(wait)` | `FOR UPDATE`; strongest row-lock mode |
| `ForShare()` / `ForShare(wait)` | `FOR SHARE`; permits compatible shared readers |
| `WithRowLock(LockStrength.NoKeyUpdate, wait)` | `FOR NO KEY UPDATE` |
| `WithRowLock(LockStrength.KeyShare, wait)` | `FOR KEY SHARE` |
| `LockWait.Wait` | Wait for an incompatible holder; the default for parameterless calls |
| `LockWait.NoWait` | Fail immediately on an incompatible holder |
| `LockWait.SkipLocked` | Omit rows that cannot be locked immediately |

`WithRowLock` also accepts `LockStrength.Update` and `LockStrength.Share`. Its strength and wait arguments are both required. Strength/wait values must be structural constants, including in compiled queries. Runtime parameters that select lock keywords in a compiled query are rejected. Repeated markers are rejected even when their modes agree.

`SkipLocked` is useful only when omitted rows are acceptable. It provides no completeness or fairness guarantee. Use PostgreSQL's [row-lock compatibility rules](https://www.postgresql.org/docs/16/explicit-locking.html#LOCKING-ROWS) when choosing a strength.

<a id="query-shapes-api"></a>
### Supported queries and mappings

The accepted grammar is closed. Each condition below must hold for the complete query:

| Part | Accepted form |
|---|---|
| Root | One ordinary entity mapped directly to one unambiguous table occurrence; materialize the entity itself |
| Tracking | Tracked or `AsNoTracking` entity queries |
| Predicate | Direct mapped scalar properties compared by equality with constants or extracted EF parameters; combine equalities with `AND` |
| Key | Primary, natural or composite predicates under the same rule; nullable and converted properties retain their explicitly verified mapping constraints |
| Marker position | Before or after an admitted `Where` |
| Terminal | `Single`, `SingleOrDefault`, `First`, `FirstOrDefault`, their async counterparts, or sync/async entity enumeration |
| Bounded ordered selection | Direct mapped scalar `OrderBy`/`OrderByDescending`, optional `ThenBy`/`ThenByDescending`, followed by one `Take(1)`; no offset |
| Reuse | `EF.CompileQuery` and `EF.CompileAsyncQuery` with constant lock options; ordinary cache reuse |
| Query tags | Ordinary constant user tags; reserved protocol material and dynamic lock-query tags are rejected |

C# promotes `short` equality operands to `int`. The validator admits the built-in methodless, non-lifted `short`-to-`int` conversion around one direct mapped member, and the matching conversion around a short constant/parameter when present. It validates the pair and preserves the expression. Other casts, computed members and user-defined conversions are rejected. An integer parameter outside the mapped `smallint` range can still raise native provider overflow; values are not narrowed by the library.

The following forms are rejected before the requested SELECT is issued:

- JOIN, LEFT JOIN, self-join and multiple targets; projections, including identity `Select`;
- `Include`, split queries, eager navigations, global filters including named filters;
- navigation/member chains, predicate subqueries, methods, `OR`, inequalities and client evaluation;
- raw SQL/function roots, views, inheritance, owned/complex, shared or split mappings;
- nested locking/pushdown, `Skip`, arbitrary or repeated `Take`, `Distinct`, grouping, aggregates, `Any`/`Count` and set operations;
- repeated lock markers, dynamic options and other unknown shapes.

A familiar operator name does not admit every overload or expression using it. Custom mappings outside the verified cases need their own provider validation. Ordered multirow array/key-set locking is outside the current scope.

<a id="selection-api"></a>
### Selection and concurrency

For an ordered single-row selection:

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var documents = await db.Documents
    .OrderBy(x => x.Id)
    .Take(1)
    .ForUpdate(LockWait.SkipLocked)
    .ToListAsync(ct);
await transaction.CommitAsync(ct);
```

The result can be empty. At READ COMMITTED, `ORDER BY` supplies no universal lock-acquisition order or guarantee that the returned row still ranks first after a lock wait and concurrent key changes. `Single` preserves native multiplicity behavior and can lock inspected rows before reporting that several rows matched. A unique predicate and the duration/order of application work remain the caller's responsibility.

<a id="transaction-api"></a>
### Transaction, cache and errors

Query construction and `ToQueryString()` may happen before a transaction begins. Actual execution requires the current explicit EF transaction on the same command connection. A correctly enlisted borrowed transaction through EF `UseTransaction` is eligible; ambient `TransactionScope` alone is unsupported. The library never begins, commits, rolls back or retries a transaction for you.

Validation runs across original expression/model, final relational scope and command execution. It checks active services, request fulfillment and the actual `Take(1)` parameter even on cached or compiled execution. Cache reuse cannot turn a later `Take(2)` into an accepted request. Internal tags are implementation details and grant no authorization; forged/reserved text is rejected even in ordinary user tags.

Cancellation propagates. PostgreSQL errors such as lock conflict (`55P03`), serialization failure (`40001`), deadlock (`40P01`) and backend termination (`57P01`) remain recognizable through native EF/Npgsql wrappers. The caller decides how to handle the failure and the transaction. See [Troubleshooting](capabilities.md#troubleshooting-api).

<a id="advisory-api"></a>
## Advisory coordination

An advisory lock represents an application-defined key. Only operations that acquire the same advisory lock participate in that protocol; unrelated SQL is not protected automatically.

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
await db.Database.AcquireTransactionAdvisoryLockAsync("document:1", ct);
var inserted = await db.InsertIgnoreAsync(
    new Document { Id = 1, Payload = "created" }, x => x.Id, ct);
await transaction.CommitAsync(ct);
```

Choose a key convention shared by all cooperating callers. The lock can serialize work before a corresponding row exists. The supported overloads return `Task`:

```csharp
await db.Database.AcquireTransactionAdvisoryLockAsync("document:1", ct);
await db.Database.AcquireTransactionAdvisoryLockAsync(123L, ct);
await db.Database.AcquireTransactionAdvisoryLockAsync(7, 123, ct);
```

Each line above is an independent overload example to execute inside a caller-owned transaction. Text keys use `pg_advisory_xact_lock(hashtextextended(key, 0))` on PostgreSQL. Hashing can collide and shares the bigint key space with the `long` overload. PostgreSQL's two-int space is separate from its bigint space. Avoid assuming that text, bigint and two-int keys with similar spelling identify the same lock.

These are blocking, transaction-scoped operations. Waiting can be canceled; PostgreSQL releases the lock at transaction completion. No session-scoped fallback, try-lock API, automatic retry or collision handling is provided.
