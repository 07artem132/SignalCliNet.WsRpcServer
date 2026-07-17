using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить SPKI→admin-principal резолв (add-invites-admin, task 2.3): діючий admin-credential → admin
/// principal (IsAdmin, без full-access); невідомий/revoked SPKI → null (deny); порожній SPKI → null.
/// </summary>
public sealed class AdminPrincipalResolverTests : IDisposable
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-adminresolve-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;

    public AdminPrincipalResolverTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], FixedTime));
    }

    [Fact]
    public void Resolve_ValidCredential_ReturnsAdminPrincipal()
    {
        _store.UpsertAdminCredential(new AdminCredentialRecord("SPKI-OK", "admin", Revoked: false, FixedTime));

        var principal = AdminPrincipalResolver.Resolve(_store, "SPKI-OK");

        Assert.NotNull(principal);
        Assert.True(principal!.IsAdmin);
        Assert.False(principal.HasFullAccess);   // R3.6: нагляд ≠ операція
        Assert.Equal("admin", principal.Name);
    }

    [Fact]
    public void Resolve_UnknownSpki_ReturnsNull()
    {
        Assert.Null(AdminPrincipalResolver.Resolve(_store, "SPKI-STRANGER"));
    }

    [Fact]
    public void Resolve_RevokedCredential_ReturnsNull()
    {
        _store.UpsertAdminCredential(new AdminCredentialRecord("SPKI-REVOKED", "admin", Revoked: false, FixedTime));
        _store.RevokeAdminCredential("SPKI-REVOKED");

        Assert.Null(AdminPrincipalResolver.Resolve(_store, "SPKI-REVOKED"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptySpki_ReturnsNull(string? spki)
    {
        Assert.Null(AdminPrincipalResolver.Resolve(_store, spki));
    }

    public void Dispose()
    {
        _store.Dispose();
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
