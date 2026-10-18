using System.Data;
using System.Data.Common;
using EntityFrameworkCore.PostgreSQL.Extensions.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace EntityFrameworkCore.PostgreSQL.Extensions.Tests.Query;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class OrderedUuidSetLockTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    private static readonly Guid ScopeA = Guid.Parse("00000000-0000-0000-0000-0000000000a0");
    private static readonly Guid ScopeB = Guid.Parse("00000000-0000-0000-0000-0000000000b0");
    private static readonly Guid IdA = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid IdB = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid IdC = Guid.Parse("00000000-0000-0000-0000-000000000003");

    [Fact]
    public async Task Ordered_set_uses_native_uuid_membership_and_returns_unique_existing_rows_in_key_order()
    {
        await SeedAsync(Row(IdA, ScopeA), Row(IdB, ScopeA), Row(IdC, ScopeA));
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var ids = new[] { IdC, IdA, IdB, IdB, Guid.NewGuid() };
        var query = context.UuidRows
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id)
            .ForUpdate();

        var generated = query.ToQueryString();
        var selected = await query.ToListAsync();
        var executed = Assert.Single(observer.Commands);

        Assert.Equal(new[] { IdA, IdB, IdC }, selected.Select(row => row.Id));
        Assert.Contains("ANY", generated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", generated, StringComparison.OrdinalIgnoreCase);
        var generatedAlias = QueryTestSupport.RootAlias(generated, "UuidRows");
        var executedAlias = QueryTestSupport.RootAlias(executed, "UuidRows");
        Assert.Contains($"ORDER BY {generatedAlias}.\"Id\"", generated, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", generated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ANY", executed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"ORDER BY {executedAlias}.\"Id\"", executed, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", executed, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(generatedAlias, executedAlias);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "ordered-uuid-native", "supported-pass",
            $"generated={OneLine(generated)};executed={OneLine(executed)};selected={string.Join(',', selected.Select(row => row.Id))};input=reversed-with-duplicate-and-missing;limit=absent");
    }

    [Fact]
    public async Task Ordered_set_preserves_empty_and_null_array_membership_results()
    {
        await SeedAsync(Row(IdA, ScopeA));
        await using var context = QueryTestSupport.CreateContext(fixture);
        await using var transaction = await context.Database.BeginTransactionAsync();

        Guid[] empty = [];
        Guid[]? nullIds = null;
        var emptyRows = await context.UuidRows.Where(row => empty.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync();
        var nullRows = await context.UuidRows.Where(row => nullIds!.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync();

        Assert.Empty(emptyRows);
        Assert.Empty(nullRows);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "ordered-uuid-empty-null", "supported-pass",
            "emptyArrayRows=0;nullArrayRows=0;nativeMembershipSemantics=preserved");
    }

    [Fact]
    public async Task Ordered_set_supports_optional_guid_prefix_equality_and_cached_or_compiled_array_parameters()
    {
        await SeedAsync(Row(IdA, ScopeA), Row(IdB, ScopeA), Row(IdC, ScopeB));
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();

        var ids = new[] { IdB, IdA, IdC };
        var query = context.UuidRows
            .Where(row => ids.Contains(row.Id) && row.ScopeId == ScopeA)
            .OrderBy(row => row.ScopeId)
            .ThenBy(row => row.Id)
            .ForUpdate();
        var sql = query.ToQueryString();
        var first = await query.ToListAsync();
        Assert.Equal(new[] { IdA, IdB }, first.Select(row => row.Id));
        var normalizedSql = OneLine(sql);
        var orderByIndex = normalizedSql.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
        Assert.True(orderByIndex >= 0);
        var orderingSql = normalizedSql[orderByIndex..];
        Assert.True(orderingSql.IndexOf("\"ScopeId\"", StringComparison.Ordinal)
            < orderingSql.IndexOf("\"Id\"", StringComparison.Ordinal));
        Assert.Equal(1, observer.Count);

        ids = [IdA];
        var cached = await query.ToListAsync();
        Assert.Equal(new[] { IdA }, cached.Select(row => row.Id));

        var compiled = EF.CompileAsyncQuery((ProbeContext db, Guid[] keys) => db.UuidRows
            .Where(row => keys.Contains(row.Id))
            .OrderBy(row => row.Id)
            .ForUpdate());
        var compiledRows = await compiled(context, [IdB]).ToListAsync();
        Assert.Equal(new[] { IdB }, compiledRows.Select(row => row.Id));
        var compiledAgain = await compiled(context, [IdC]).ToListAsync();
        Assert.Equal(new[] { IdC }, compiledAgain.Select(row => row.Id));
        Assert.Equal(4, observer.Count);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "ordered-uuid-prefix-cache", "supported-pass",
            $"sql={OneLine(sql)};prefix=ScopeId;optionalEquality=ScopeA;cachedIds={string.Join(',', cached.Select(row => row.Id))};compiledIds={string.Join(',', compiledRows.Select(row => row.Id))},{string.Join(',', compiledAgain.Select(row => row.Id))};commands={observer.Count}");
    }

    [Fact]
    public async Task Ordered_set_unsupported_shapes_fail_before_issuing_a_command_in_a_transaction()
    {
        await SeedAsync(Row(IdA, ScopeA), Row(IdB, ScopeA));
        var observer = new SqlCommandObserver();
        await using var context = QueryTestSupport.CreateContext(fixture, observer);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var ids = new[] { IdA, IdB };

        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).Take(1).ForUpdate().ToListAsync(), "complete entity enumeration");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).Take(2).ForUpdate().ToListAsync(), "complete entity enumeration");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().FirstAsync(), "complete entity enumeration");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().SingleAsync(), "complete entity enumeration");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).Skip(1).ForUpdate().ToListAsync(), "outside the admitted root lock grammar");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).Select(row => row).ForUpdate().ToListAsync(), "outside the admitted root lock grammar");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderByDescending(row => row.Id).ForUpdate().ToListAsync(), "ascending OrderBy");
        await AssertRejectedAsync(observer, () => context.CompositeUuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.ScopeId).ThenBy(row => row.Id).ForUpdate().ToListAsync(), "direct unconverted Guid primary key");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id) && ids.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync(), "only one UUID-array membership");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).WithRowLock(LockStrength.Share, LockWait.Wait).ToListAsync(), "requires Update with Wait");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate(LockWait.SkipLocked).ToListAsync(), "requires Update with Wait");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate(LockWait.NoWait).ToListAsync(), "requires Update with Wait");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .ForUpdate().ToListAsync(), "requires ascending Id");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.ScopeId).ThenBy(row => row.Id).ThenBy(row => row.Id)
            .ForUpdate().ToListAsync(), "requires ascending Id");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id).ThenBy(row => row.Id).ForUpdate().ToListAsync(), "separate direct Guid property");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.ScopeId))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync(), "direct unconverted Guid primary key");
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => ids.Contains(row.ConvertedGuid))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync(), "direct unconverted Guid primary key");
        var list = ids.ToList();
        await AssertRejectedAsync(observer, () => context.UuidRows.Where(row => list.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync(), "captured or compiled Guid[]");
        await AssertRejectedAsync(observer, () => context.UuidRows.Join(context.UuidRows, row => row.Id, other => other.Id,
                (row, other) => row).Where(row => ids.Contains(row.Id)).ForUpdate().ToListAsync(),
            "outside the admitted root lock grammar");

        Assert.Equal(0, observer.Count);
        await transaction.RollbackAsync();
        QueryTestSupport.WriteOutcome(output, "ordered-uuid-rejections", "explicit-reject-as-designed",
            $"rejectedShapes=19;issuedCommands={observer.Count}");
    }

    [Fact]
    public async Task Context_factory_and_pool_reuse_uuid_set_queries_without_leaking_lock_intent()
    {
        await SeedAsync(Row(IdA, ScopeA), Row(IdB, ScopeA), Row(IdC, ScopeB));
        var observer = new SqlCommandObserver();
        var services = new ServiceCollection();
        services.AddDbContextFactory<ProbeContext>(options =>
        {
            options.UseProviderTestWarnings()
                .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
                .AddInterceptors(observer);
        });
        await using var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IDbContextFactory<ProbeContext>>();
        await using (var context = await factory.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var ids = new[] { IdB, IdA };
            var locked = await context.UuidRows.Where(row => ids.Contains(row.Id))
                .OrderBy(row => row.Id).ForUpdate().ToListAsync();
            Assert.Equal(new[] { IdA, IdB }, locked.Select(row => row.Id));
            Assert.Contains("FOR UPDATE OF", observer.Commands.Single(), StringComparison.Ordinal);
            await transaction.CommitAsync();
        }

        await using (var context = await factory.CreateDbContextAsync())
        {
            var ordinary = await context.UuidRows.Where(row => row.Id == IdC).SingleAsync();
            Assert.Equal(IdC, ordinary.Id);
            Assert.DoesNotContain("FOR UPDATE", observer.Commands.Last(), StringComparison.Ordinal);
        }

        var pooledOptions = new DbContextOptionsBuilder<ProbeContext>().UseProviderTestWarnings()
            .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UsePostgreSqlExtensions())
            .AddInterceptors(observer)
            .Options;
        var pool = new PooledDbContextFactory<ProbeContext>(pooledOptions, poolSize: 1);
        await using (var context = await pool.CreateDbContextAsync())
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var ids = new[] { IdC };
            var locked = await context.UuidRows.Where(row => ids.Contains(row.Id))
                .OrderBy(row => row.Id).ForUpdate().ToListAsync();
            Assert.Equal(new[] { IdC }, locked.Select(row => row.Id));
            await transaction.CommitAsync();
        }

        await using (var context = await pool.CreateDbContextAsync())
        {
            var ordinary = await context.UuidRows.Where(row => row.Id == IdA).SingleAsync();
            Assert.Equal(IdA, ordinary.Id);
            Assert.DoesNotContain("FOR UPDATE", observer.Commands.Last(), StringComparison.Ordinal);
        }

        Assert.Equal(4, observer.Count);
        QueryTestSupport.WriteOutcome(output, "ordered-uuid-factory-pool", "supported-pass",
            $"factoryLocked={OneLine(observer.Commands[0])};factoryOrdinary={OneLine(observer.Commands[1])};poolLocked={OneLine(observer.Commands[2])};poolOrdinary={OneLine(observer.Commands[3])};commandCount={observer.Count}");
    }

    [Fact]
    public async Task Disjoint_ordered_sets_are_lockable_in_the_same_time_window()
    {
        await SeedAsync(Row(IdA, ScopeB), Row(IdB, ScopeA), Row(IdC, ScopeB));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var firstIds = new[] { IdA };
        await using var first = fixture.CreateContext(enableExtensions: true);
        await using var firstTransaction = await first.Database.BeginTransactionAsync(deadline.Token);
        var firstRows = await first.UuidRows.Where(row => firstIds.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync(deadline.Token);

        var secondIds = new[] { IdC };
        await using var second = fixture.CreateContext(enableExtensions: true);
        await using var secondTransaction = await second.Database.BeginTransactionAsync(deadline.Token);
        var secondRows = await second.UuidRows.Where(row => secondIds.Contains(row.Id))
            .OrderBy(row => row.Id).ForUpdate().ToListAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);

        Assert.Equal(new[] { IdA }, firstRows.Select(row => row.Id));
        Assert.Equal(new[] { IdC }, secondRows.Select(row => row.Id));
        await secondTransaction.RollbackAsync(deadline.Token);
        await firstTransaction.RollbackAsync(deadline.Token);
        QueryTestSupport.WriteOutcome(output, "ordered-uuid-disjoint-sets", "supported-pass",
            $"firstSet={string.Join(',', firstRows.Select(row => row.Id))};secondSet={string.Join(',', secondRows.Select(row => row.Id))};bothHeldBeforeRollback=true");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordered_set_acquires_locks_in_sql_order_and_leaves_later_rows_available(bool useGuidPrefix)
    {
        await SeedAsync(Row(IdA, ScopeB), Row(IdB, ScopeA), Row(IdC, ScopeB));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var cancellationToken = deadline.Token;

        await using var holder = fixture.CreateContext();
        await using var holderTransaction = await holder.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var blockedId = useGuidPrefix ? IdA : IdB;
        var precedingId = useGuidPrefix ? IdB : IdA;
        var expectedOrder = useGuidPrefix ? new[] { IdB, IdA, IdC } : new[] { IdA, IdB, IdC };
        await LockIdAsync(holder.Database.GetDbConnection(), holderTransaction.GetDbTransaction(), blockedId, cancellationToken);
        var holderBackend = await BackendIdAsync(holder.Database.GetDbConnection(), holderTransaction.GetDbTransaction(), cancellationToken);

        var observer = new SqlCommandObserver();
        await using var waiter = QueryTestSupport.CreateContext(fixture, observer);
        await using var waiterTransaction = await waiter.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var waiterBackend = await BackendIdAsync(waiter.Database.GetDbConnection(), waiterTransaction.GetDbTransaction(), cancellationToken);
        Assert.NotEqual(holderBackend, waiterBackend);

        Task<List<UuidProbeRow>>? waitingQuery = null;
        var holderCommitted = false;
        var waiterRolledBack = false;
        try
        {
            var ids = new[] { IdC, IdB, IdA };
            var query = waiter.UuidRows.Where(row => ids.Contains(row.Id));
            query = useGuidPrefix
                ? query.OrderBy(row => row.ScopeId).ThenBy(row => row.Id)
                : query.OrderBy(row => row.Id);
            waitingQuery = query.ForUpdate().ToListAsync(cancellationToken);
            var waiterEvent = await WaitForLockWaitStateAsync(fixture, waiterBackend, TimeSpan.FromSeconds(8), cancellationToken);

            var precedingConflict = await Assert.ThrowsAsync<PostgresException>(() => TryLockIdAsync(precedingId, cancellationToken));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, precedingConflict.SqlState);
            await TryLockIdAsync(IdC, cancellationToken);

            await holderTransaction.CommitAsync(cancellationToken);
            holderCommitted = true;
            var selected = await waitingQuery.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            Assert.Equal(expectedOrder, selected.Select(row => row.Id));
            var sql = Assert.Single(observer.Commands);
            Assert.Contains("FOR UPDATE OF", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
            await waiterTransaction.RollbackAsync(cancellationToken);
            waiterRolledBack = true;
            QueryTestSupport.WriteOutcome(output,
                useGuidPrefix ? "ordered-uuid-contention-two-columns" : "ordered-uuid-contention-one-column",
                "supported-pass",
                $"backendIds={holderBackend},{waiterBackend};waitEvent={waiterEvent};precedingRow=blocked-by-waiter;middleRow=held-then-released;laterRow=available-while-waiting;selected={string.Join(',', selected.Select(row => row.Id))};sql={OneLine(sql)}");
        }
        finally
        {
            deadline.Cancel();
            if (waitingQuery is not null)
            {
                try
                {
                    await waitingQuery.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // Preserve the assertion or provider failure that entered cleanup.
                }
            }

            if (!holderCommitted)
            {
                await holderTransaction.RollbackAsync(CancellationToken.None);
            }

            if (!waiterRolledBack)
            {
                await waiterTransaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    private async Task AssertRejectedAsync(SqlCommandObserver observer, Func<Task> execute, string reason)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(execute);
        Assert.StartsWith("The PostgreSQL row-lock request cannot be fulfilled:", exception.Message, StringComparison.Ordinal);
        Assert.Contains(reason, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, observer.Count);
    }

    private async Task SeedAsync(params UuidProbeRow[] rows)
    {
        await fixture.ResetAsync();
        await using var context = fixture.CreateContext();
        context.UuidRows.AddRange(rows);
        await context.SaveChangesAsync();
    }

    private static UuidProbeRow Row(Guid id, Guid scope)
        => new() { Id = id, ScopeId = scope, Payload = $"uuid-{id:N}" };

    private static async Task LockIdAsync(DbConnection connection, DbTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM \"UuidRows\" WHERE \"Id\" = @id FOR UPDATE";
        AddId(command, id);
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task TryLockIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT 1 FROM \"UuidRows\" WHERE \"Id\" = @id FOR UPDATE NOWAIT";
        AddId(command, id);
        _ = await command.ExecuteScalarAsync(cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
    }

    private static void AddId(DbCommand command, Guid id)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
    }

    private static async Task<int> BackendIdAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_backend_pid()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> WaitForLockWaitStateAsync(
        PostgreSqlFixture fixture,
        int backendId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT wait_event_type FROM pg_stat_activity WHERE pid = @pid";
            command.Parameters.AddWithValue("pid", backendId);
            if (await command.ExecuteScalarAsync(deadline.Token) is string waitEvent && waitEvent == "Lock")
            {
                return waitEvent;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"PostgreSQL backend {backendId} did not enter a lock wait within {timeout}.");
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
