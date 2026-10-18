# Documentation changes

## Readers and structure

Write for a .NET developer who knows basic EF Core and wants to use the library. Start with the task, show the smallest useful example, then explain the result, transaction ownership, constraints and errors. Use plain, complete sentences. Define unfamiliar terms at first use. State facts directly; avoid rhetorical contrasts such as "not X, but Y".

Current documentation lives in the following places:

| Place | Purpose |
|---|---|
| `README.md`, `README.ru.md` | Short project introduction, one example, status and links |
| `docs/index.md`, `docs/ru/index.md` | Documentation entry points |
| `docs/getting-started.md` and Russian counterpart | Package acquisition, setup and a complete first example |
| `docs/query-integration.md` and Russian counterpart | Locking: row locks and advisory coordination |
| `docs/internal-mechanisms.md` and Russian counterpart | Data and transactions: insert-ignore, server time and read-only work |
| `docs/capabilities.md` and Russian counterpart | Configuration, compatibility probe and troubleshooting |
| `docs/compatibility.md` and Russian counterpart | Version baseline, support limits and preview policy |
| `docs/architecture.md` and Russian counterpart | Maintainer architecture and integration boundaries |
| `CONTRIBUTING.md`, `SECURITY.md`, `CHANGELOG.md` | Contribution workflow, actual security reporting status and user-relevant changes |

Group related capabilities into existing guides. Add a page only when a topic needs independent navigation and has enough coherent content. Keep each contract in one owning section; link to it from other pages. Read the whole affected section before and after editing. Remove replaced explanations and reconcile definitions, exceptions and neighboring examples.

## English and Russian

Maintain matching paths and explicit heading IDs in `docs/` and `docs/ru/`. English is the default locale; Russian is a complete user-facing counterpart. Update both languages in the same change. Preserve identical API names, code, dependency versions, preconditions, exceptions and support limits. Translate explanation naturally; do not mix editorial languages inside a page. Maintainer instructions under `.agents/` and repository policies may remain English.

Examples should use neutral names and the actual public API. Keep paired examples in sync and compile changed C# examples when applicable. State required context, mapping, database setup and package source. Mark partial snippets clearly. Never present an unpublished package as available on NuGet.org. Keep installation and version instructions consistent with project metadata.

## Technical truth and history

Describe current behavior and adopted support boundaries. Retain important details: supported/rejected query and model shapes, transaction/connection ownership, native failure behavior, defaults/NULL, volatile clock semantics and unverified compatibility. Future ideas need an explicit requested design task; they must not appear as supported behavior.

Keep proof methodology in contributor instructions, and summarize actual verification in the response or PR. Do not add unrequested reports, evidence files, execution logs, review transcripts, plans, handoff summaries, acceptance stories, test totals, run IDs or corrective-pass narratives to Git. Temporary output belongs in ignored `artifacts/` or outside the checkout. Remove obsolete material from the active tree after integrating any still-needed contracts; Git history retains its provenance. Do not replace deleted historical documents with an archive or redirect pages merely to retain them. Retain technical versions, fixture pins and runtime/domain history when they are needed for current instructions or contracts.

## Check and deliver

Run `npm ci` and `npm run docs:build` in this repository; the build checks both locales and internal page links. Preview with `npm run docs:preview`. Check the affected pages in a browser, including Russian navigation, the locale switch, anchors, local search, narrow screens and the repository base path. Review prose and translation equivalence separately from build success. Check relative README/policy links on GitHub as well as website links. Report actual checks and limitations in the response.

The website is built from `docs/`; `.agents/` and generated output are outside its content. Keep dependency versions and the lockfile reproducible. The docs workflow validates pull requests and pushes; deployment is a separate manual action documented in `CONTRIBUTING.md`. Adding or editing workflow configuration does not establish that the site has been deployed.
