namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Resolved on-disk paths of the auto-generated secret material on the data volume. Consumed by
/// later capabilities (transport TLS, authn token/abuse peppers, admin mTLS) — this change only
/// provisions and persists them.
/// </summary>
/// <remarks>
/// Ці шляхи вказують на файли з <c>0600</c>-правами (Unix). Самі секрети НІКОЛИ не логуються і не
/// потрапляють у env/шари образу — лише сюди, як шляхи на томі.
/// </remarks>
public sealed record ProvisionedSecrets
{
    /// <summary>PEM-encoded CA certificate (public — used to build client trust stores).</summary>
    public required string CaCertificatePath { get; init; }

    /// <summary>PEM-encoded CA private key (<c>0600</c>).</summary>
    public required string CaKeyPath { get; init; }

    /// <summary>PEM-encoded server (serverAuth) certificate.</summary>
    public required string ServerCertificatePath { get; init; }

    /// <summary>PEM-encoded server private key (<c>0600</c>).</summary>
    public required string ServerKeyPath { get; init; }

    /// <summary>PEM-encoded admin (clientAuth) certificate — the mTLS admin bundle (S3 canon).</summary>
    public required string AdminClientCertificatePath { get; init; }

    /// <summary>PEM-encoded admin private key (<c>0600</c>).</summary>
    public required string AdminClientKeyPath { get; init; }

    /// <summary>File holding the base64 token pepper (<c>0600</c>).</summary>
    public required string TokenPepperPath { get; init; }

    /// <summary>File holding the base64 abuse pepper (<c>0600</c>).</summary>
    public required string AbusePepperPath { get; init; }
}
