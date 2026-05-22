using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using WsRpcServer.Core;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalDevicesRpc : ISignalDevices, IRpcService;