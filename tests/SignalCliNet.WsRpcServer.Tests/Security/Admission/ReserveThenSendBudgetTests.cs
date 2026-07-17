using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить <see cref="ReserveThenSendBudget"/> (W13/G3/D15): атомарний RMW під конкуренцією (рівно один
/// true під бюджетом 1), блок-резервацію гранулою K, форфейт невитраченого залишку на «рестарті»
/// (ніколи не over-send) та монотонний перехід вікна.
/// </summary>
public class ReserveThenSendBudgetTests : IDisposable
{
    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-budget-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    public ReserveThenSendBudgetTests() => Directory.CreateDirectory(_dataDir);

    private DurableStore NewStore()
    {
        var store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        store.Initialize();
        return store;
    }

    private static ReserveThenSendBudget NewBudget(
        IDurableStore store, TimeProvider time, int aggregate, int blockSize, int windowMinutes = 60) =>
        new(
            store,
            Options.Create(new AdmissionOptions
            {
                AggregateBudgetPerWindow = aggregate,
                ReserveBlockSize = blockSize,
                BudgetWindowMinutes = windowMinutes,
            }),
            time,
            NullLogger<ReserveThenSendBudget>.Instance);

    [Fact]
    public void G3_ConcurrentReserve_UnderBudgetOne_GrantsExactlyOne()
    {
        using var store = NewStore();
        var budget = NewBudget(store, new TestTimeProvider(FixedTime), aggregate: 1, blockSize: 1);

        var granted = 0;
        Parallel.For(0, 32, _ =>
        {
            if (budget.TryReserveUnit())
                Interlocked.Increment(ref granted);
        });

        Assert.Equal(1, granted);
        Assert.Equal(1, store.CountBudgetBlocks(budget.CurrentWindowKey));
    }

    [Fact]
    public void BlockReservation_DrawsBlocksOfK_UntilCeiling()
    {
        using var store = NewStore();
        var budget = NewBudget(store, new TestTimeProvider(FixedTime), aggregate: 10, blockSize: 5);
        var windowKey = budget.CurrentWindowKey;

        // Перші 5 юнітів покриті одним durable-блоком.
        for (var i = 0; i < 5; i++)
            Assert.True(budget.TryReserveUnit());
        Assert.Equal(1, store.CountBudgetBlocks(windowKey));

        // 6-й юніт бере другий блок.
        Assert.True(budget.TryReserveUnit());
        Assert.Equal(2, store.CountBudgetBlocks(windowKey));

        // Юніти 7..10 добирають другий блок; 11-й — стеля вичерпана.
        for (var i = 0; i < 4; i++)
            Assert.True(budget.TryReserveUnit());
        Assert.False(budget.TryReserveUnit());
        Assert.Equal(2, store.CountBudgetBlocks(windowKey));
    }

    [Fact]
    public void Restart_ForfeitsUnspentRemainder_NeverOverSends()
    {
        using var store = NewStore();
        var time = new TestTimeProvider(FixedTime);

        // Інстанс 1: взяв блок K=5, витратив лише 2 юніти.
        var first = NewBudget(store, time, aggregate: 10, blockSize: 5);
        Assert.True(first.TryReserveUnit());
        Assert.True(first.TryReserveUnit());
        Assert.Equal(1, store.CountBudgetBlocks(first.CurrentWindowKey));

        // «Рестарт»: новий інстанс на тому ж сторі у тому ж вікні. Залишок блоку 1 (3 юніти) форфейтиться —
        // доступно лише 5 (другий блок), НЕ 8.
        var second = NewBudget(store, time, aggregate: 10, blockSize: 5);
        var granted = 0;
        while (second.TryReserveUnit())
            granted++;

        Assert.Equal(5, granted);
        Assert.Equal(2, store.CountBudgetBlocks(second.CurrentWindowKey)); // рівно 2 блоки = 10 юнітів, ніколи більше
    }

    [Fact]
    public void WindowTransition_ResetsBudget_AndRotatesWindowKey()
    {
        using var store = NewStore();
        var time = new TestTimeProvider(FixedTime);
        var budget = NewBudget(store, time, aggregate: 5, blockSize: 5, windowMinutes: 60);

        var key1 = budget.CurrentWindowKey;
        for (var i = 0; i < 5; i++)
            Assert.True(budget.TryReserveUnit());
        Assert.False(budget.TryReserveUnit()); // вікно вичерпане

        // Перехід вікна детектується монотонним годинником (D15).
        time.Advance(TimeSpan.FromMinutes(61));

        var key2 = budget.CurrentWindowKey;
        Assert.NotEqual(key1, key2);
        Assert.True(budget.TryReserveUnit()); // бюджет рефілиться у новому вікні
    }

    [Fact]
    public void RetryAfterSeconds_IsPositive_WithinWindow()
    {
        using var store = NewStore();
        var budget = NewBudget(store, new TestTimeProvider(FixedTime), aggregate: 1, blockSize: 1, windowMinutes: 60);

        Assert.True(budget.RetryAfterSeconds() > 0);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання темп-каталогу
        }
    }
}
