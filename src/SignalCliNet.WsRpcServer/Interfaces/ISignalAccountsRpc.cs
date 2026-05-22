using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using WsRpcServer.Core;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalAccountsRpc : ISignalAccounts, IRpcService;