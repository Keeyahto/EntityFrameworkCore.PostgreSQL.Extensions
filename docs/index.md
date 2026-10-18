# EntityFrameworkCore.PostgreSQL.Extensions

Use PostgreSQL primitives through typed EF Core APIs: coordinate concurrent work, insert a row while ignoring one selected unique conflict, read server time and inspect database requirements.

The library ships as one `net10.0` assembly in the `EntityFrameworkCore.PostgreSQL.Extensions` namespace. These guides describe **0.1.0-preview.6**. Public NuGet availability is not established; [Getting started](getting-started.md) explains how to build and install a local package. Preview APIs may change.

<a id="choose-guide"></a>
## Choose a guide

| Your task | Read |
|---|---|
| Set up the package and run a complete example | [Getting started](getting-started.md) |
| Protect existing rows or coordinate work by a key | [Locking](query-integration.md) |
| Insert without overwriting a duplicate, use server time or mark a transaction read-only | [Data and transactions](internal-mechanisms.md) |
| Configure contexts, check database requirements or diagnose failures | [Configuration and diagnostics](capabilities.md) |
| Choose runtime, provider and server versions | [Compatibility](compatibility.md) |
| Understand the implementation before contributing | [Architecture](architecture.md) |

<a id="shared-boundaries"></a>
## Shared behavior

You create and complete transactions. Row locks and advisory locks require an explicit current EF transaction; insert-ignore and a server clock read can also run as standalone statements. Cancellation is forwarded to PostgreSQL, and native provider errors propagate. The library adds no retry or application recovery policy.

Row-lock queries have a deliberately bounded grammar. A marked query outside that grammar fails explicitly. Ordinary queries retain native EF/Npgsql behavior. Read the relevant guide before composing locking with other LINQ operators.

The reference dependency/server combination and unverified configurations are listed in [Compatibility](compatibility.md). The library can be developed and used independently of any particular application.
