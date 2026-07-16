using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Runs the startup-security guarantees before the RPC transport comes up: the D4 fail-closed bind
/// assertion (belt-and-suspenders next to <c>ValidateOnStart</c>) and the G1 single-instance lock.
/// Registered ahead of the RPC hosted service so it starts first and refuses start deterministically.
/// </summary>
/// <remarks>
/// Порядок кроків детермінований: спершу D4-перевірка (не біндимось у небезпечній конфігурації),
/// потім G1-lock (не стартуємо другим екземпляром на тому ж томі). Обидва — refuse-start із чітким
/// логом і БЕЗ доступу до БД/стану. Наступні секції (auto-gen секретів, durable-стор) додають свої
/// кроки сюди ж, зберігаючи цей порядок.
/// </remarks>
public sealed class SecureDeploymentHostedService(
    IOptions<PersistenceOptions> persistenceOptions,
    IOptions<TransportSecurityOptions> transportOptions,
    SecretMaterialProvisioner secretProvisioner,
    ILogger<SecureDeploymentHostedService> logger)
    : IHostedService, IDisposable
{
    private readonly PersistenceOptions _persistence =
        persistenceOptions?.Value ?? throw new ArgumentNullException(nameof(persistenceOptions));

    private readonly TransportSecurityOptions _transport =
        transportOptions?.Value ?? throw new ArgumentNullException(nameof(transportOptions));

    private readonly SecretMaterialProvisioner _secretProvisioner =
        secretProvisioner ?? throw new ArgumentNullException(nameof(secretProvisioner));

    private readonly ILogger<SecureDeploymentHostedService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private DataDirectoryLock? _lock;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // D4 (fail-closed): відмова стартувати у небезпечній bind-конфігурації. Дублює ValidateOnStart,
        // але гарантує детермінований refuse ДО будь-якого захоплення ресурсів.
        var (ok, error) = StartupSecurityValidator.ValidateBinding(
            _transport.Host, _transport.AllowNonLoopback, _transport.Tls.Enabled);
        if (!ok)
        {
            _logger.LogCritical("Відмова старту (D4, fail-closed): {Reason}", error);
            throw new InvalidOperationException($"D4 fail-closed startup: {error}");
        }

        // G1: exclusive lock на data-dir. Другий екземпляр → refuse-start.
        var dataDirectory = ResolveDataDirectory(_persistence.DataDirectory);
        _lock = DataDirectoryLock.Acquire(dataDirectory);
        _logger.LogInformation(
            "Захоплено single-instance lock на каталозі даних (G1); замок: {LockFile}", _lock.LockFilePath);

        // Auto-gen секретів (persist-once, 0600): CA + server/admin cert + пеппери на томі (R3.4/R3.7).
        var secrets = _secretProvisioner.Provision(dataDirectory, _transport.Tls.Hostname);
        _logger.LogInformation(
            "Секрети готові на томі (CA/server/admin cert + пеппери); каталог: {SecretsDir}",
            Path.GetDirectoryName(secrets.CaCertificatePath));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        ReleaseLock();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => ReleaseLock();

    private void ReleaseLock()
    {
        if (_lock is null)
            return;

        _lock.Dispose();
        _lock = null;
        _logger.LogInformation("Звільнено single-instance lock на каталозі даних (G1).");
    }

    /// <summary>
    /// Resolves the data directory to an absolute path (relative paths resolve against the content root).
    /// </summary>
    internal static string ResolveDataDirectory(string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
}
