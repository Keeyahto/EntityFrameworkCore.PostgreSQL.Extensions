# Architecture

The library provides reusable PostgreSQL-specific extensions through native EF-facing APIs. Public behavior is owned by [Locking](query-integration.md), [Data and transactions](internal-mechanisms.md) and [Configuration and diagnostics](capabilities.md). This page explains the implementation boundaries for maintainers.

<a id="package-design"></a>
## Package and integration mechanisms

One packable runtime project targets `net10.0` and exposes the `EntityFrameworkCore.PostgreSQL.Extensions` namespace. Internal feature directories separate responsibilities; conceptual layers do not require extra assemblies. The repository also contains one test project, an isolated package-consumer sample and a Markdown documentation website. The library uses Apache-2.0.

| Mechanism | Responsibility | Integration point |
|---|---|---|
| Query integration | Row locks on an exact mapped table occurrence | Typed query marker, expression interceptor, structural SQL generator and execution validation |
| Scalar expression integration | PostgreSQL clock expressions | Additive EF `DbFunction` mapping |
| Transaction-scoped commands | Advisory locks and read-only command | Parameterized/native `DatabaseFacade` commands in a borrowed transaction |
| Metadata-aware DML | One-row selected-key insert-ignore | Finalized EF mappings, quoted identifiers, typed parameters and direct execution |

The typed clock read and capability probe use narrow connection/transaction-aware paths. Mechanisms remain independent; they require no common operation interface or dispatcher. Native EF/Npgsql support is preferred when it supplies the necessary semantics. Extend the relevant mechanism when adding capabilities, and justify any new abstraction with actual shared behavior.

<a id="query-design"></a>
## Row-lock pipeline

```text
typed IQueryable marker
  -> original expression and finalized-model validation
  -> immutable reserved intent tag
  -> native Npgsql translation
  -> final SELECT/table/column/selection-bound validation
  -> structural FOR ... OF quoted-generated-alias clause
  -> command-time fulfillment/service/transaction/bound validation
  -> PostgreSQL execution in the caller's transaction
```

The marker constructs a call to its own method definition. Without registration, it remains untranslatable. The expression interceptor validates exact method identities, overloads, properties, model roots and the closed grammar before carrying intent forward. The native query translator remains installed. Only `IQuerySqlGeneratorFactory` is replaced. The generator validates the final relational scope independently and emits a structural lock on the exact generated root alias.

The command interceptor checks matching intent/fulfillment, actual services, attached transaction/connection and execution-time selection bounds on synchronous, asynchronous, cached and compiled paths. It never modifies `CommandText`. An internal tag pair alone establishes no valid lock. Reserved-protocol spoofing/collisions are rejected, while ordinary constant tags survive. State is immutable and query-specific; correctness does not depend on mutable global or `AsyncLocal` state.

EF may extract a literal `Take(1)` into a parameter before compilation interception. The private bound identity must survive translation, and its value must be one at execution. Original-expression validation and a final root-looking SELECT are both needed to prevent widened or redirected locking. Query cache, factories, pools and provider service composition preserve this behavior.

The provider generator seam is version-sensitive. Keep EF1001 suppression local to its adapter. Competing translator/generator replacements and manual internal service-provider composition fail explicitly. Additive plugins remain functional; ordinary unmarked queries preserve native SQL/results.

<a id="execution-design"></a>
## Execution and resource ownership

Operations require the Npgsql EF provider and connection. An existing transaction is used only when the underlying `NpgsqlTransaction` is attached to that connection. Row/advisory locks and read-only commands require an explicit transaction. Insert-ignore, clock reads and the compatibility probe may execute standalone with their documented boundaries.

Connection acquisitions are balanced through EF; helpers preserve previously open connections. The caller owns transaction begin/commit/rollback, cancellation policy, retries and recovery. PostgreSQL and provider errors propagate with their native SQLSTATEs and wrappers. No application permission, lifecycle, revision, uncertain-commit or host-readiness policy enters the library.

Insert-ignore resolves metadata and typed values before opening the connection, uses native atomic conflict handling and leaves tracking untouched. Clock mapping preserves volatile evaluation semantics. Advisory text hashing remains server-side. The probe observes configured SQL target and actual server/installed-extension facts independently and never reconfigures the provider.

<a id="maintenance-design"></a>
## Maintenance boundaries

The finite public grammar and mapping contracts are maintained in the guides. A new query form, mapping or dependency support claim needs an explicit documented scope change and risk-appropriate provider/package verification. Build, generated SQL, unit tests, actual PostgreSQL behavior and isolated package consumption establish different facts; maintain native controls and negative checks appropriate to the guarantee.

Use [Contributing](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/CONTRIBUTING.md) and the [code instructions](https://github.com/Keeyahto/EntityFrameworkCore.PostgreSQL.Extensions/blob/develop/.agents/code.md) for the workflow. Current guides describe contracts. Superseded experiments and execution records remain in Git history. Documentation builds and deployment are independent of runtime/package verification.

Generic ORM/UoW, universal SQL AST, hidden transaction/retry managers, command-text lock patching, automatic SQL-target selection and host lifecycle orchestration are outside this design. Compatibility and release policy are described in [Compatibility](compatibility.md).
