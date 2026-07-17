using System.ComponentModel.DataAnnotations;

namespace SignalCliNet.WsRpcServer.Deployment;

/// <summary>
/// Options describing where the server keeps its durable, security-sensitive state — the single
/// data directory that holds the instance lockfile (G1), the SQLite durable store and the
/// auto-generated secret material (CA / certs / peppers).
/// </summary>
/// <remarks>
/// Каталог даних МУСИТЬ лежати на платформному encrypted-томі (R3.7/S2 — at-rest шифрування —
/// це відповідальність платформи, НЕ app-LUKS). Тут ми лише фіксуємо шлях; шифрування тому —
/// операційна передумова (див. deploy/DEPLOYMENT.md).
/// </remarks>
public sealed class PersistenceOptions
{
    /// <summary>Configuration section name (<c>Persistence</c>).</summary>
    public const string SectionName = "Persistence";

    /// <summary>
    /// Path to the data directory. Relative paths resolve against the process content root.
    /// In the container this is the mounted volume (e.g. <c>/data</c>).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string DataDirectory { get; set; } = "data";
}
