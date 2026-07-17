namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// The confirm-seam the group-claim scanner calls when it matches a pending claim-code in a shared-bot
/// group message (add-group-claim-receive, tasks 1.2/2.2). Decouples the receive-side scanner from the
/// claim store/service — the scanner knows nothing about consume-and-bind, it just reports a match.
/// </summary>
/// <remarks>
/// R3.5/W25-inbound: сканер — єдиний consumer shared-bot-потоку через per-account роутер; він ізольований
/// від гарячого RPC-шляху й від claim-сховища саме цим інтерфейсом. Реалізація
/// (<see cref="GroupClaimService"/>) робить атомарний consume-and-bind (W20).
/// </remarks>
public interface IGroupClaimConfirmer
{
    /// <summary>
    /// Atomically consumes the claim <paramref name="codeHash"/> and, on success, creates the
    /// <c>(identity, group, anchorAci)</c> binding (T2): group and anchor are fixed here by the scanner.
    /// </summary>
    /// <param name="codeHash">At-rest hash of the matched code (computed by the scanner).</param>
    /// <param name="groupId">The group the code appeared in (fixes the binding's group; T3).</param>
    /// <param name="anchorAci">The ACI of the code's inserter (fixes the binding's anchor; T2).</param>
    /// <returns><c>true</c> if this call consumed the claim and created a binding; otherwise <c>false</c>.</returns>
    bool Confirm(string codeHash, string groupId, string anchorAci);
}
