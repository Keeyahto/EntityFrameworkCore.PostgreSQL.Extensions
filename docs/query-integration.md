# Query integration contract

The selected design is a typed row-lock marker, an expression interceptor, a reserved internal intent tag and structural Npgsql SQL generation. Npgsql's native query translator remains active. The [first-production contract](briefs/production-contract.md) owns exact public signatures, registration, the finite query/model scope and compatibility target. Actual production admission requires its provider and isolated-package evidence. Experimental comparisons belong to [research evidence](experiments/query-carriers.md).

## 1. Lock-request invariant

A marked query has exactly two legitimate outcomes: PostgreSQL locks the intended root occurrence under the requested strength/wait mode and the caller's transaction, or execution fails explicitly before returning data as locked. Missing configuration, unsupported transformations, query-cache reuse and cancellation preserve this invariant.

The initial scope identifies one ordinary mapped root table occurrence. General JOIN/projection/multi-target locking and ordered key-set protocols are outside the selected scope. An entity type or the first table alias alone does not establish occurrence identity. The library accepts no raw user alias/table name as a target.

## 2. Query pipeline

```text
typed IQueryable marker
  -> original expression and finalized-model validation
  -> immutable internal intent/tag
  -> native Npgsql translation
  -> final SELECT/table/scope validation
  -> structural FOR ... OF quoted-generated-alias clause
  -> command-time fulfillment/service/transaction/bound validation
  -> PostgreSQL execution in caller-owned transaction
```

The public extension constructs a call to its own method definition. Without the extension, this marker remains untranslatable. Internal tags carry typed intent; they grant no public authorization and reject spoofing/collisions, including ordinary user comments containing reserved protocol material. Preserve ordinary constant user tags.

Strength and wait options are structural constants, including in compiled queries. Repeated requests, whether identical or conflicting, are rejected. Intent is immutable and query-specific; no mutable provider-global or AsyncLocal state controls correctness.

## 3. Original expression and model

Validate the exact ordinary entity-query root, method identities/overloads, argument structure, direct mapped property identity and closed grammar in the first-production contract. An operator name alone is insufficient. Equality/AND predicates admit scalar mapped properties against constants or extracted EF query parameters; arbitrary predicate subqueries, navigation/member chains, method predicates and client evaluation are rejected. Validate terminal predicate overloads too.

Validate finalized relational mapping: one direct table and unambiguous entity occurrence; reject inherited/split/shared/view/function/raw roots, unsupported complex mappings, eager-loading transformations and all declared global filters, including named filters. A root-looking final SELECT does not prove an admissible original expression.

EF extracts ordinary captured values and may parameterize a literal Take(1) before the compilation interceptor. Preserve exact private selection-bound identity through intent and fulfillment. Validate a parameterized bound equals one at actual execution; reject Take(2) through a warmed cache. Compiled constants and native First/Single limit composition require their own structural and execution oracles. No expression evaluator or arbitrary predicate compilation is introduced.

## 4. Final SQL scope

Render the lock structurally within the relevant SELECT using its validated table occurrence and quoted generated alias. Nested SELECTs, pruning, joins, set operations, raw/function tables, offsets, grouping and unadmitted projections must not redirect it. Independently inspect the final relevant relational tree, column ownership and selection bound. A valid original marker does not exempt the final tree from validation.

PostgreSQL locking without OF can affect more tables than intended; emit the exact root alias. ORDER BY at READ COMMITTED gives no universal lock-acquisition order or post-wait ranking guarantee. SKIP LOCKED can omit occupied rows. Preserve native terminal multiplicity/empty semantics; Single may inspect/lock multiple rows before reporting multiplicity.

## 5. Execution and service composition

Query construction and ToQueryString may precede a transaction. Actual execution requires the current explicit EF transaction reference on the same command connection. A correctly enlisted borrowed transaction is eligible; ambient TransactionScope alone is unsupported. The library begins, commits, retries and releases no caller transaction.

The generator produces one matching request/fulfillment record. The command interceptor validates this record, effective service identities, exact transaction/connection and selection-bound parameters on every sync/async execution, including cached/compiled queries. It never rewrites CommandText. Paired markers alone are insufficient evidence of real PostgreSQL behavior.

Repeated registration preserves one configuration. Reject conflicting generator/translator replacements before registration, during repeated registration and after it. Manual internal service-provider composition is unsupported for this plugin. Contexts/factories/pools preserve options/cache isolation; additive Npgsql translations remain functional. Ordinary unmarked queries preserve native SQL/results.

Native PostgreSQL errors remain recognizable through native EF/Npgsql wrappers: lock conflict, serialization failure, deadlock, cancellation and connection termination are technical outcomes. The extension supplies no domain error, retry policy or uncertain-commit classification.

## 6. Evidence and maintenance

Required evidence combines structural SQL assertions with real two-session observations: exact target/non-target locks, strength/wait compatibility, release on commit/rollback, cancellation, borrowed transactions, missing configuration, replaced services, cache/compiled/factory/pool reuse and unsupported forms. Negative shape tests run inside a valid transaction, assert the intended cause and observe zero issued commands. Retain native SQL controls and native-feature-off behavior as independent oracles.

The provider-internal generator factory remains version-sensitive. Keep EF1001 suppression local to that adapter, record dependency/server sets and test the built package in an isolated consumer. Source compilation implies no compatibility range, platform or mapping support. [Query gates](query-gates.md) and [compatibility](compatibility.md) retain their evidence boundaries.
