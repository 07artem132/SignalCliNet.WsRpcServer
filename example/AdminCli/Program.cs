using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using StreamJsonRpc;

// admin-cli — reference JSON-RPC 2.0 клієнт mTLS admin-порту SignalCliNet.WsRpcServer (task 3.4).
// Спілкується з ISignalAdminRpc (createInvite/revokeIdentity) + listAccounts/ping через wss + client-cert.
// Транспорт/довіра дзеркалять тест-harness (SecureAdminPortSmokeTests/AdminMtlsDodTests):
// SocketsHttpHandler.SslOptions з ClientCertificates + CustomRootTrust-ланцюгом до ca.crt.
return await RunAsync(args).ConfigureAwait(false);

static async Task<int> RunAsync(string[] args)
{
    if (args.Length == 0 || IsHelp(args[0]))
    {
        PrintUsage();
        return args.Length == 0 ? 1 : 0;
    }

    var command = args[0];

    CliOptions opts;
    try
    {
        opts = CliOptions.Parse(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // audit-dump — окремо: серверного admin audit-read RPC НЕМАЄ, і ми його НЕ вигадуємо.
    // Tamper-evident audit-trail (audit_log, hash-chain) інспектується на боці сервера, не через цей канал.
    if (string.Equals(command, "audit-dump", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(
            "audit-dump: сервер не експонує admin audit-read RPC. Tamper-evident audit-trail " +
            "(audit_log, hash-chain) читається на боці сервера з durable-БД. " +
            "TODO: підключити цю команду лише коли/якщо upstream додасть audit-read admin-метод.");
        return 2;
    }

    try
    {
        await using var conn = await AdminConnection.OpenAsync(opts, cts.Token).ConfigureAwait(false);
        var rpc = conn.Rpc;

        switch (command)
        {
            case "ping":
                var pong = await rpc.InvokeWithCancellationAsync<JsonElement>("ping", null, cts.Token)
                    .ConfigureAwait(false);
                Console.WriteLine(pong.ToString());
                return 0;

            case "invite":
                // createInvite(ttlSeconds?, maxAttempts?): null → сервер бере дефолт з конфігу (AdminOptions).
                var inviteArgs = new { ttlSeconds = opts.Ttl, maxAttempts = opts.MaxAttempts };
                var invite = await rpc.InvokeWithParameterObjectAsync<JsonElement>("createInvite", inviteArgs, cts.Token)
                    .ConfigureAwait(false);
                Console.WriteLine("Створено one-time інвайт (передайте код користувачу out-of-band):");
                // Код — секрет для передачі новому юзеру; друкуємо навмисно. Приватний ключ — НІКОЛИ.
                Console.WriteLine($"  код:        {invite.GetProperty("code").GetString()}");
                Console.WriteLine($"  дійсний до: {invite.GetProperty("expiresAt").GetString()}");
                return 0;

            case "revoke":
                if (opts.Positional is null)
                {
                    Console.Error.WriteLine("revoke: потрібен <identityId>. Приклад: admin-cli revoke user-123");
                    return 1;
                }

                var revoke = await rpc.InvokeWithParameterObjectAsync<JsonElement>(
                    "revokeIdentity", new { identityId = opts.Positional }, cts.Token).ConfigureAwait(false);
                Console.WriteLine(
                    $"Відкликано identity {revoke.GetProperty("identityId").GetString()} " +
                    $"(revoked={revoke.GetProperty("revoked").GetBoolean()}).");
                return 0;

            case "accounts":
                // listAccounts не має account-аргумента; admin-principal бачить усі акаунти (R3.6).
                var accounts = await rpc.InvokeWithCancellationAsync<JsonElement>("listAccounts", null, cts.Token)
                    .ConfigureAwait(false);
                Console.WriteLine(JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true }));
                return 0;

            default:
                Console.Error.WriteLine($"Невідома команда: {command}");
                PrintUsage();
                return 1;
        }
    }
    catch (RemoteInvocationException ex)
    {
        // Помилка від сервера: напр. -32001 (не-admin), -32602 (InvalidParams). Друкуємо код+повідомлення.
        Console.Error.WriteLine($"RPC-помилка {ex.ErrorCode}: {ex.Message}");
        return 3;
    }
    catch (Exception ex) when (
        ex is HttpRequestException or WebSocketException or IOException or AuthenticationException
            or System.Net.Sockets.SocketException)
    {
        Console.Error.WriteLine($"Помилка підключення до admin-порту: {ex.Message}");
        Console.Error.WriteLine(
            "Перевірте --url, --cert/--key/--ca (mTLS client-cert) і що admin-порт запущено (Server:Admin).");
        return 4;
    }
}

static bool IsHelp(string a) => a is "-h" or "--help" or "help";

static void PrintUsage()
{
    Console.WriteLine(
        """
        admin-cli — reference JSON-RPC 2.0 клієнт mTLS admin-порту SignalCliNet.WsRpcServer.

        Використання:
          admin-cli <команда> [--url wss://host:9443] [--cert PATH] [--key PATH] [--ca PATH]

        Команди:
          invite [--ttl СЕК] [--max-attempts N]   createInvite → друкує код + термін дії
          revoke <identityId>                      revokeIdentity → каскад revoke токенів+device-секрету
          accounts                                 listAccounts → перелік акаунтів (admin бачить усі, R3.6)
          ping                                     перевірка живості admin-каналу
          audit-dump                               немає серверного audit-read RPC (див. README)

        Дефолти секретів (авто-провіжн SecretMaterialProvisioner на томі <dataDir>/secrets/):
          --url  wss://127.0.0.1:9443
          --cert /data/secrets/admin-client.crt
          --key  /data/secrets/admin-client.key
          --ca   /data/secrets/ca.crt

        Приватність: приватний ключ НІКОЛИ не друкується; код інвайту друкується (він для передачі юзеру).
        """);
}

/// <summary>Розібрані аргументи CLI: параметри підключення + опції команд.</summary>
internal sealed class CliOptions
{
    public required Uri Url { get; init; }
    public required string CertPath { get; init; }
    public required string KeyPath { get; init; }
    public required string CaPath { get; init; }
    public int? Ttl { get; init; }
    public int? MaxAttempts { get; init; }

    /// <summary>Перший позиційний аргумент (напр. identityId для <c>revoke</c>).</summary>
    public string? Positional { get; init; }

    public static CliOptions Parse(string[] args)
    {
        const string defaultSecrets = "/data/secrets";
        var url = "wss://127.0.0.1:9443";
        string? cert = null, key = null, ca = null, positional = null;
        int? ttl = null, maxAttempts = null;

        // args[0] — команда; прапорці/позиційні — з індексу 1.
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--url": url = Next(args, ref i, a); break;
                case "--cert": cert = Next(args, ref i, a); break;
                case "--key": key = Next(args, ref i, a); break;
                case "--ca": ca = Next(args, ref i, a); break;
                case "--ttl": ttl = ParseInt(Next(args, ref i, a), a); break;
                case "--max-attempts": maxAttempts = ParseInt(Next(args, ref i, a), a); break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"Невідомий прапорець: {a}");
                    positional ??= a; // перший позиційний (identityId)
                    break;
            }
        }

        return new CliOptions
        {
            Url = new Uri(url),
            CertPath = cert ?? Path.Combine(defaultSecrets, "admin-client.crt"),
            KeyPath = key ?? Path.Combine(defaultSecrets, "admin-client.key"),
            CaPath = ca ?? Path.Combine(defaultSecrets, "ca.crt"),
            Ttl = ttl,
            MaxAttempts = maxAttempts,
            Positional = positional,
        };
    }

    private static string Next(string[] args, ref int i, string flag) =>
        ++i < args.Length ? args[i] : throw new ArgumentException($"{flag} потребує значення.");

    private static int ParseInt(string v, string flag) =>
        int.TryParse(v, out var n) ? n : throw new ArgumentException($"{flag}: очікується ціле число, отримано '{v}'.");
}

/// <summary>
/// Живе mTLS-підключення до admin-порту: ClientWebSocket (client-cert + CustomRootTrust до ca.crt) під
/// StreamJsonRpc-стеком (System.Text.Json-форматтер). Транспорт дзеркалить тест-harness.
/// </summary>
internal sealed class AdminConnection : IAsyncDisposable
{
    public JsonRpc Rpc { get; }

    private readonly ClientWebSocket _ws;
    private readonly List<IDisposable> _cleanup;

    private AdminConnection(JsonRpc rpc, ClientWebSocket ws, List<IDisposable> cleanup)
    {
        Rpc = rpc;
        _ws = ws;
        _cleanup = cleanup;
    }

    public static async Task<AdminConnection> OpenAsync(CliOptions opts, CancellationToken ct)
    {
        var cleanup = new List<IDisposable>();

        var clientCert = LoadClientCertificate(opts.CertPath, opts.KeyPath);
        cleanup.Add(clientCert);
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(opts.CaPath));
        cleanup.Add(ca);

        // Приватний CA деплою не в системному trust → довіряємо server-cert-у через CustomRootTrust-ланцюг
        // до нашого CA; RevocationMode=NoCheck (авто-CA без CRL/OCSP, як BuildTransport сервера).
        var chainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        chainPolicy.CustomTrustStore.Add(ca);

        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = chainPolicy,
                ClientCertificates = [clientCert], // mTLS: admin client-cert = креденшел
            },
        };
        var invoker = new HttpMessageInvoker(handler); // диспоузить handler за собою
        cleanup.Add(invoker);

        var ws = new ClientWebSocket();
        try
        {
            await ws.ConnectAsync(opts.Url, invoker, ct).ConfigureAwait(false);
        }
        catch
        {
            ws.Dispose();
            foreach (var d in cleanup)
                d.Dispose();
            throw;
        }

        // Сервер читає сирі байти незалежно від типу WS-кадру, тож StreamJsonRpc-форматтер (Text) сумісний.
        var messageHandler = new WebSocketMessageHandler(ws, new SystemTextJsonFormatter());
        var rpc = new JsonRpc(messageHandler);
        rpc.StartListening();
        return new AdminConnection(rpc, ws, cleanup);
    }

    private static X509Certificate2 LoadClientCertificate(string certPath, string keyPath)
    {
        if (!File.Exists(certPath))
            throw new FileNotFoundException($"Не знайдено client-cert: {certPath}", certPath);
        if (!File.Exists(keyPath))
            throw new FileNotFoundException($"Не знайдено client-key: {keyPath}", keyPath);

        // PEM cert+key → PKCS#12 → LoadPkcs12: приватний ключ у придатному для TLS-рукостискання вигляді
        // (ідентично LoadClientCertificate тест-harness'а). Ключ у пам'яті; на консоль НЕ виводиться.
        using var pem = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), password: null);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // best-effort ґречне закриття — ігноруємо помилки на shutdown
        }

        Rpc.Dispose(); // диспоузить messageHandler + websocket
        foreach (var d in _cleanup)
            d.Dispose();
    }
}
