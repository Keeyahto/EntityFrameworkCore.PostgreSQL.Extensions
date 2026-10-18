# Locking

Use a row lock to protect existing mapped rows while your transaction reads and changes them. Use a transaction-scoped advisory lock to coordinate cooperating operations by a key, including work for which no row exists yet. Both forms require the caller's current explicit EF transaction and are released by PostgreSQL when that transaction completes.

Examples use the model from [Getting started](getting-started.md). Register row-lock support as shown in [Configuration](capabilities.md#registration-api); advisory locks need only the Npgsql provider.

| Method | Use it to |
|---|---|
| [ForUpdate](#for-update-api) | Exclusively lock selected rows |
| [ForShare](#for-share-api) | Acquire shared row locks |
| [WithRowLock](#with-row-lock-api) | Choose any supported row-lock strength |
| [AcquireTransactionAdvisoryLockAsync](#advisory-api) | Coordinate by a text, bigint or two-int key |

SQL below corresponds to this model and the [reference versions](compatibility.md). Internal lock-protocol comments are omitted and whitespace is formatted for reading. `$1`, `$2` denote PostgreSQL parameters; their values are explained beside each example. Transaction begin/commit are omitted from SQL listings. Aliases and parameter notation can vary with provider versions.

<a id="row-locks-api"></a>
## Row locks

The three row-lock methods return `IQueryable<T>` and preserve the entity type. They mark the query; execution happens at a terminal such as `SingleAsync` or enumeration. All three use the [same supported grammar](#query-shapes-api).

<a id="for-update-api"></a>
### ForUpdate

Use `.ForUpdate()` or `.ForUpdate(LockWait wait)` to request PostgreSQL's strongest row-lock mode. The parameterless overload uses `Wait`; the following example uses `NoWait` to fail immediately on an incompatible holder.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var document = await db.Documents
    .Where(x => x.Id == 1)
    .ForUpdate(LockWait.NoWait)
    .SingleAsync(ct);
await transaction.CommitAsync(ct);
```

**SQL**

```sql
SELECT d."Id", d."Payload"
FROM "Documents" AS d
WHERE d."Id" = 1
LIMIT 2
FOR UPDATE OF d NOWAIT;
```

`LIMIT 2` comes from EF's `SingleAsync`: EF can detect multiple matches. It is separate from the lock mode. The selected entity is tracked by default; subsequent changes still need `SaveChangesAsync` inside the caller's transaction.

This requests `FOR UPDATE OF` the generated root alias. If another transaction holds an incompatible lock, `NoWait` raises PostgreSQL's lock-not-available error. A query with a lock marker either obtains the requested lock on the validated root occurrence or fails explicitly. It cannot successfully return data as locked after silently dropping the request.

<a id="for-share-api"></a>
### ForShare

Use `.ForShare()` or `.ForShare(LockWait wait)` for a shared row lock. Compatible shared holders may coexist; conflicting modifications wait or fail according to the selected wait mode. The default is `Wait`.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var document = await db.Documents
    .Where(x => x.Id == 1)
    .ForShare()
    .SingleAsync(ct);
await transaction.CommitAsync(ct);
```

**SQL**

```sql
SELECT d."Id", d."Payload"
FROM "Documents" AS d
WHERE d."Id" = 1
LIMIT 2
FOR SHARE OF d;
```

The lock lasts until transaction completion. Tracking and lock strength are independent: `AsNoTracking` is also eligible under the shared query grammar.

<a id="with-row-lock-api"></a>
### WithRowLock

Use `.WithRowLock(LockStrength strength, LockWait wait)` when you need an explicit strength. Both arguments are required. This example chooses `NoKeyUpdate` and ordinary waiting.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
var document = await db.Documents
    .Where(x => x.Id == 1)
    .WithRowLock(LockStrength.NoKeyUpdate, LockWait.Wait)
    .SingleAsync(ct);
await transaction.CommitAsync(ct);
```

**SQL**

```sql
SELECT d."Id", d."Payload"
FROM "Documents" AS d
WHERE d."Id" = 1
LIMIT 2
FOR NO KEY UPDATE OF d;
```

`KeyShare` produces `FOR KEY SHARE OF d`; `Update` and `Share` correspond to the dedicated methods. All modes keep the same transaction and query-shape requirements.

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
| Predicate | Direct mapped scalar properties compared by equality with constants or extracted EF parameters; combine with `AND`. The complete ordered UUID-set form additionally admits its one membership predicate |
| Key | Primary, natural or composite predicates under the same rule; nullable and converted properties retain their explicitly verified mapping constraints |
| Marker position | Before or after an admitted `Where` |
| Terminal | `Single`, `SingleOrDefault`, `First`, `FirstOrDefault`, their async counterparts, or sync/async entity enumeration; complete UUID sets require enumeration |
| Bounded ordered selection | Direct mapped scalar `OrderBy`/`OrderByDescending`, optional `ThenBy`/`ThenByDescending`, followed by one `Take(1)`; no offset |
| Complete ordered UUID set | One captured or compiled `Guid[]` parameter used with `Enumerable.Contains<Guid>` on the direct, unconverted Guid primary key; ascending key order, optionally preceded by one ascending direct Guid column; full entity enumeration with `Update` and `Wait`, without a limit |
| Reuse | `EF.CompileQuery` and `EF.CompileAsyncQuery` with constant lock options; ordinary cache reuse |
| Query tags | Ordinary constant user tags; reserved protocol material and dynamic lock-query tags are rejected |

C# promotes `short` equality operands to `int`. The validator admits the built-in methodless, non-lifted `short`-to-`int` conversion around one direct mapped member, and the matching conversion around a short constant/parameter when present. It validates the pair and preserves the expression. Other casts, computed members and user-defined conversions are rejected. An integer parameter outside the mapped `smallint` range can still raise native provider overflow; values are not narrowed by the library.

The following forms are rejected before the requested SELECT is issued:

- JOIN, LEFT JOIN, self-join and multiple targets; projections, including identity `Select`;
- `Include`, split queries, eager navigations, global filters including named filters;
- navigation/member chains, predicate subqueries, unadmitted methods, `OR`, inequalities and client evaluation;
- raw SQL/function roots, views, inheritance, owned/complex, shared or split mappings;
- nested locking/pushdown, `Skip`, arbitrary or repeated `Take`, `Distinct`, grouping, aggregates, `Any`/`Count` and set operations;
- repeated lock markers, dynamic options and other unknown shapes.

A familiar operator name does not admit every overload or expression using it. Custom mappings outside the verified cases need their own provider validation. The UUID-set form below is the only admitted complete ordered multirow selection.

<a id="ordered-set-api"></a>
### Complete ordered UUID sets

Use `Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ForUpdate()` for an array `Guid[] ids` and an entity whose single-property primary key `Id` maps directly to `uuid`. For a stable parent-first protocol, use `OrderBy(x => x.ParentId).ThenBy(x => x.Id)` with a directly mapped, unconverted Guid `ParentId`. Materialize the complete entities with `ToListAsync` or enumeration. These partial snippets require an appropriately mapped UUID entity; the integer-key `Document` example above does not supply that model.

The array may be extracted from a closure or passed to a compiled query. Exactly one UUID membership predicate is admitted, optionally combined with the existing equality predicates through `AND`. The primary key is the last ordering term. All ordering terms must be ascending; the caller must preserve their values during the locking protocol. The library validates the original ordering and its final relational target, and leaves predicate translation to Npgsql.

This form uses `Update` with `Wait`. Other strengths/wait modes, `Take`, `Skip`, scalar terminals such as `First`/`Single`, projections, JOINs, composite primary keys and arbitrary collection expressions are rejected. Existing ordered `Take(1)` queries retain their separate grammar.

Duplicate array values do not duplicate rows; missing IDs produce no rows and an empty array produces an empty result. The caller checks expected membership and cardinality. Row locks protect existing selected rows; they do not prevent insertion of absent IDs. Concurrent changes to ordering keys invalidate application-level ordering assumptions. Keep the selected keys immutable for the duration of cooperating operations.

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

**SQL** (`$1 = 1` for `Take(1)`):

```sql
SELECT d."Id", d."Payload"
FROM "Documents" AS d
ORDER BY d."Id"
LIMIT $1
FOR UPDATE OF d SKIP LOCKED;
```

The result can be empty. At READ COMMITTED, `ORDER BY` supplies no universal lock-acquisition order or guarantee that the returned row still ranks first after a lock wait and concurrent key changes. `Single` preserves native multiplicity behavior and can lock inspected rows before reporting that several rows matched. A unique predicate and the duration/order of application work remain the caller's responsibility.

<a id="transaction-api"></a>
### Transaction, cache and errors

Query construction and `ToQueryString()` may happen before a transaction begins. Actual execution requires the current explicit EF transaction on the same command connection. A correctly enlisted borrowed transaction through EF `UseTransaction` is eligible; ambient `TransactionScope` alone is unsupported. The library never begins, commits, rolls back or retries a transaction for you.

Validation runs across original expression/model, final relational scope and command execution. It checks active services, request fulfillment and the actual `Take(1)` parameter even on cached or compiled execution. Cache reuse cannot turn a later `Take(2)` into an accepted request. Internal tags are implementation details and grant no authorization; forged/reserved text is rejected even in ordinary user tags.

Cancellation propagates. PostgreSQL errors such as lock conflict (`55P03`), serialization failure (`40001`), deadlock (`40P01`) and backend termination (`57P01`) remain recognizable through native EF/Npgsql wrappers. The caller decides how to handle the failure and the transaction. See [Troubleshooting](capabilities.md#troubleshooting-api).

<a id="advisory-api"></a>
## AcquireTransactionAdvisoryLockAsync

An advisory lock represents an application-defined key. Only operations that acquire the same advisory lock participate in that protocol; unrelated SQL is not protected automatically. All three overloads return `Task` and require a current explicit EF transaction.

<a id="advisory-text-api"></a>
### Text key

Use the string overload for a shared application key convention. The lock can coordinate work before a corresponding row exists.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
await db.Database.AcquireTransactionAdvisoryLockAsync("document:1", ct);
await transaction.CommitAsync(ct);
```

**SQL** (`$1 = "document:1"`):

```sql
SELECT pg_advisory_xact_lock(hashtextextended($1, 0));
```

Hashing happens on PostgreSQL with seed zero. It is not collision-free and shares the bigint key space with the `long` overload.

<a id="advisory-bigint-api"></a>
### Bigint key

Use the `long` overload when your protocol already has a 64-bit lock key.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
await db.Database.AcquireTransactionAdvisoryLockAsync(123L, ct);
await transaction.CommitAsync(ct);
```

**SQL** (`$1 = 123`, type `bigint`):

```sql
SELECT pg_advisory_xact_lock($1);
```

<a id="advisory-pair-api"></a>
### Two-int key

Use the two-`int` overload for a pair of 32-bit keys, for example a category and an object key.

**C#**

```csharp
await using var transaction = await db.Database.BeginTransactionAsync(ct);
await db.Database.AcquireTransactionAdvisoryLockAsync(7, 123, ct);
await transaction.CommitAsync(ct);
```

**SQL** (`$1 = 7`, `$2 = 123`, both type `integer`):

```sql
SELECT pg_advisory_xact_lock($1, $2);
```

PostgreSQL's two-int space is separate from its bigint space. Avoid assuming that text, bigint and two-int keys with similar spelling identify the same lock.

These are blocking, transaction-scoped operations. Waiting can be canceled; PostgreSQL releases the lock at transaction completion. No session-scoped fallback, try-lock API, automatic retry or collision handling is provided.
