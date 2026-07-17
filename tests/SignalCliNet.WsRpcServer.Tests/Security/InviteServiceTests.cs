using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="InviteService"/> (tasks 1.1/1.3): create→redeem happy-path; анти-oracle
/// (unknown/expired/consumed/over-cap → ідентичний null); G12 (паралельний redeem не обходить
/// atomic consume); формат коду ≥96-bit у візуальних блоках та round-trip нормалізації.
/// </summary>
public class InviteServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-invite-svc-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(FixedTime);
    private readonly InviteService _service;

    public InviteServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _service = new InviteService(_store, _time, NullLogger<InviteService>.Instance);
    }

    [Fact]
    public void Create_ReturnsCode_InVisualBlocks_AtLeast96Bit()
    {
        var creation = _service.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        // Візуальні блоки: код містить дефіси-роздільники.
        Assert.Contains('-', creation.Code);

        // 128-bit (16 байт) у Crockford-base32 → рівно 26 символів корисного навантаження (≥96-bit).
        var normalizedLen = creation.Code.Count(char.IsLetterOrDigit);
        Assert.Equal(26, normalizedLen);

        // Запис збережено: hash співпадає, не consumed, cap правильний.
        Assert.Equal(creation.Record.CodeHash, InviteService.ComputeCodeHash(creation.Code));
        var stored = _store.GetInvite(creation.Record.CodeHash);
        Assert.NotNull(stored);
        Assert.False(stored!.Consumed);
        Assert.Equal(3, stored.MaxAttempts);
    }

    [Fact]
    public void Create_ThenTryRedeem_Succeeds_AndBinds()
    {
        var creation = _service.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        var redemption = _service.TryRedeem(creation.Code, "new-id", FixedTime);

        Assert.NotNull(redemption);
        Assert.Equal("admin-1", redemption!.CreatedBy);
        Assert.Equal(creation.Record.CodeHash, redemption.CodeHash);

        var stored = _store.GetInvite(creation.Record.CodeHash);
        Assert.True(stored!.Consumed);
        Assert.Equal("new-id", stored.ConsumedBy);
    }

    [Fact]
    public void TryRedeem_ReformattedCode_StillSucceeds()
    {
        var creation = _service.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        // Клієнт ввів код інакше оформленим: нижній регістр, пробіли замість дефісів.
        var reformatted = creation.Code.Replace('-', ' ').ToLowerInvariant();

        Assert.NotNull(_service.TryRedeem(reformatted, "new-id", FixedTime));
    }

    [Fact]
    public void TryRedeem_AllFailureModes_ReturnIdenticalNull()
    {
        // --- unknown ---
        var unknown = _service.TryRedeem("ZZZZ-ZZZZ-ZZZZ", "id-a", FixedTime);

        // --- expired ---
        var expiredCreation = _service.Create("admin-1", TimeSpan.FromMinutes(1), maxAttempts: 3);
        var expired = _service.TryRedeem(expiredCreation.Code, "id-a", FixedTime.AddMinutes(2));

        // --- consumed ---
        var consumedCreation = _service.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);
        Assert.NotNull(_service.TryRedeem(consumedCreation.Code, "id-a", FixedTime));
        var consumed = _service.TryRedeem(consumedCreation.Code, "id-b", FixedTime);

        // --- over-cap (сідимо attempt_count == max через прямий insert із відомим hash) ---
        const string overCapCode = "OVER-CAP-CODE-1234";
        _store.InsertInvite(new InviteRecord(
            InviteService.ComputeCodeHash(overCapCode), "admin-1", Consumed: false,
            AttemptCount: 3, MaxAttempts: 3, FixedTime.AddHours(1), ConsumedBy: null, FixedTime));
        var overCap = _service.TryRedeem(overCapCode, "id-a", FixedTime);

        // Anti-oracle: усі чотири невдачі — ІДЕНТИЧНИЙ null (жоден не розкриває причину).
        Assert.Null(unknown);
        Assert.Null(expired);
        Assert.Null(consumed);
        Assert.Null(overCap);
    }

    [Fact]
    public void TryRedeem_ParallelSameCode_ExactlyOneWinner()
    {
        // G12/W20: паралельний redeem одного коду під maxAttempts=3 → рівно один непорожній результат,
        // cap не обходиться гонкою; усі спроби порахувались.
        const int attempts = 24;
        var creation = _service.Create("admin-1", TimeSpan.FromHours(1), maxAttempts: 3);

        var winners = 0;
        Parallel.For(0, attempts, i =>
        {
            if (_service.TryRedeem(creation.Code, $"id-{i}", FixedTime) is not null)
                Interlocked.Increment(ref winners);
        });

        Assert.Equal(1, winners);
        Assert.Equal(attempts, _store.GetInvite(creation.Record.CodeHash)!.AttemptCount);
    }

    [Theory]
    [InlineData("ABCD-1234", "abcd1234")]        // регістр + роздільники
    [InlineData("ABCD 1234", "ABCD1234")]        // пробіли-роздільники
    [InlineData("0O1I1L", "001111")]             // фолд сплутуваних символів O→0, I/L→1
    public void ComputeCodeHash_NormalizesEquivalentForms(string a, string b)
    {
        Assert.Equal(InviteService.ComputeCodeHash(a), InviteService.ComputeCodeHash(b));
    }

    [Fact]
    public void ComputeCodeHash_DifferentCodes_DifferentHash()
    {
        Assert.NotEqual(InviteService.ComputeCodeHash("ABCD-1234"), InviteService.ComputeCodeHash("ABCD-1235"));
    }

    [Fact]
    public void Create_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() => _service.Create("", TimeSpan.FromHours(1), 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.Create("admin", TimeSpan.Zero, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.Create("admin", TimeSpan.FromHours(1), 0));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _store.Dispose();
        SqliteConnection.ClearAllPools();
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
