using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Persistence;
using WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// First-boot admin bootstrap (add-invites-admin, task 2.2): idempotently ensures a <c>role=admin</c>
/// identity exists and binds it to the auto-provisioned admin client-cert's SPKI in the durable
/// <c>admin_credentials</c> table. Runs on every start after the durable store is initialised.
/// </summary>
/// <remarks>
/// <b>Ідемпотентність.</b> Якщо у сторі вже є identity з <c>role=admin</c> — переюзаємо її (не плодимо
/// других адмінів); інакше створюємо <c>id="admin"</c>. Mapping SPKI→identity — <c>INSERT OR IGNORE</c>,
/// тож повторний boot того самого cert-а нічого не змінює (зберігає revoked-прапорець).
///
/// <b>G5.</b> Admin cert-bundle уже лежить у <c>0600</c>-файлі на томі (SecretMaterialProvisioner) — сюди
/// ми беремо лише ПУБЛІЧНИЙ cert (для SPKI) і НІКОЛИ не логуємо матеріал ключа: лише факт
/// «admin-credential provisioned».
/// </remarks>
public sealed class AdminBootstrapService(IDurableStore store, ILogger<AdminBootstrapService> logger)
{
    /// <summary>The fixed admin identity id used when no admin identity yet exists.</summary>
    public const string DefaultAdminIdentityId = "admin";

    private const string AdminRole = "admin";

    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ILogger<AdminBootstrapService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Ensures the admin identity + SPKI credential mapping (idempotent). No-op logging of any key material.
    /// </summary>
    /// <param name="adminClientCertificatePath">Path to the auto-provisioned admin client cert (PEM, public).</param>
    public void Bootstrap(string adminClientCertificatePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminClientCertificatePath);

        if (!File.Exists(adminClientCertificatePath))
        {
            // Секрети завжди провіжняться до цього кроку; відсутність cert-а — аномалія, лог і no-op
            // (не валимо старт: admin-порт просто не матиме credential і відхилятиме конекти fail-closed).
            _logger.LogWarning(
                "Admin bootstrap: admin-cert відсутній на томі — пропуск (admin-порт відхилятиме конекти).");
            return;
        }

        // Обчислюємо SPKI-SHA-256 admin-cert-а тим самим методом, що й mTLS-резолвер сесії (стабільний
        // при renewal cert-а з тим самим ключем). Лише публічний cert — приватний ключ не потрібен.
        var spki = NodeIdentity.ComputeSpkiThumbprint(
            X509Certificate2.CreateFromPem(File.ReadAllText(adminClientCertificatePath)));

        var now = DateTimeOffset.UtcNow;

        // Ідемпотентно: переюзаємо наявну admin-identity, інакше створюємо "admin".
        var existingAdmin = _store.GetIdentities().FirstOrDefault(i =>
            string.Equals(i.Role, AdminRole, StringComparison.Ordinal));

        string adminIdentityId;
        if (existingAdmin is null)
        {
            adminIdentityId = DefaultAdminIdentityId;
            _store.UpsertIdentity(new IdentityRecord(adminIdentityId, AdminRole, [], now));
            _logger.LogInformation("Admin bootstrap: створено admin-identity ({IdentityId}).", adminIdentityId);
        }
        else
        {
            adminIdentityId = existingAdmin.Id;
        }

        // Прив'язка SPKI→admin-identity (INSERT OR IGNORE — переюз наявного mapping).
        _store.UpsertAdminCredential(new AdminCredentialRecord(spki, adminIdentityId, Revoked: false, now));

        // G5: жодного матеріалу у лозі — лише факт.
        _logger.LogInformation("Admin bootstrap: admin-credential provisioned (identity {IdentityId}).", adminIdentityId);
    }
}
