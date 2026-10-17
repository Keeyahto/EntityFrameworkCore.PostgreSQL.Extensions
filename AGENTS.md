# Library engineering instructions

Read [README](README.md), [architecture](docs/architecture.md), [query contract](docs/query-integration.md), [PoC](docs/poc-plan.md), [compatibility](docs/compatibility.md) and [contribution workflow](CONTRIBUTING.md) for the actual change. These local documents and source/tests are sufficient for work in this repository.

Keep one runtime package/assembly, native EF-facing typed APIs and four independent mechanisms. Native support is preferred when semantically sufficient. The source contains experimental implementations; bounded real-PostgreSQL checks cover selected query forms, while the production carrier, supported API scope, and package compatibility range remain subject to maintainer decision. Distinguish source compilation, unit evidence, real-provider evidence, packed-consumer evidence and publication.

Do not implement generic ORM/UoW, universal SQL AST, operation registry, hidden transaction/retry or string-based lock rewriting. Preserve no-silent-unlock, exact target occurrence, borrowed transactions, immutable query state and native SQLSTATE. Changes require relevant positive/negative tests and documentation of actual behavior. Keep private consumer governance, data and credentials outside this repository. Package identity and publication require maintainer decisions.
