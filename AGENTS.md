# Library engineering instructions

Read [README](README.md), [architecture](docs/architecture.md), [query contract](docs/query-integration.md), [PoC](docs/poc-plan.md), [compatibility](docs/compatibility.md) and [contribution workflow](CONTRIBUTING.md) for the actual change. These local documents and source/tests are sufficient for work in this repository.

Keep one runtime package/assembly, native EF-facing typed APIs and four independent mechanisms. Native support is preferred when semantically sufficient. The [first-production contract](docs/briefs/production-contract.md) selects the expression-interceptor design, finite public scope and initial evidence target. Implementation, provider evidence, packed-consumer acceptance and publication are distinct outcomes; design approval supplies none of those results.

Do not implement generic ORM/UoW, universal SQL AST, operation registry, hidden transaction/retry or string-based lock rewriting. Preserve no-silent-unlock, exact target occurrence, borrowed transactions, immutable query state and native SQLSTATE. Changes require relevant positive/negative tests and documentation of actual behavior. Keep private consumer governance, data and credentials outside this repository. Package identity and publication require maintainer decisions.
