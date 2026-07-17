using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCli.Exceptions;
using SignalCli.Models.Rpc;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Security.Fail;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Fail;

/// <summary>
/// Пінить V10 catch-матрицю (<see cref="UpstreamSendFailureMapper"/>, tasks 2.1/2.2/2.3): кожен тип →
/// очікуваний санітизований код БЕЗ PII; -5/-6 тригерять global-pause, -4 ні; restart-cancel
/// відрізняється від client-cancel.
/// </summary>
public sealed class UpstreamSendFailureMapperTests
{
    private const string Pii = "+15551234567";

    private static UpstreamSendFailureMapper NewMapper(RecordingGate gate) =>
        new(gate, Options.Create(new GlobalPauseOptions { BasePauseSeconds = 60, MaxPauseSeconds = 900 }),
            NullLogger<UpstreamSendFailureMapper>.Instance);

    private static JsonRpcError Err(int code) => new() { Code = code, Message = $"upstream failure for {Pii}" };

    [Fact]
    public void RateLimit_MapsTo32005_TriggersPause_NoPii()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        var mapped = mapper.Map(new RateLimitException(Err(-5)), CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(AdmissionErrorCodes.RateLimited, (int)rpc.ErrorCode);
        Assert.DoesNotContain(Pii, rpc.Message, StringComparison.Ordinal);
        Assert.Equal(["rate-limit"], gate.Pauses);
        Assert.IsType<RateLimitErrorData>(rpc.ErrorData);
    }

    [Fact]
    public void Captcha_MapsTo32005_TriggersPause_NoPii()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        var mapped = mapper.Map(new CaptchaRequiredException(Err(-6)), CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(AdmissionErrorCodes.RateLimited, (int)rpc.ErrorCode);
        Assert.DoesNotContain(Pii, rpc.Message, StringComparison.Ordinal);
        Assert.Equal(["captcha"], gate.Pauses);
    }

    [Fact]
    public void UntrustedIdentity_MapsTo32012_NoPause()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        var mapped = mapper.Map(new UntrustedIdentityException(Err(-4)), CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(FailPathErrorCodes.UntrustedIdentity, (int)rpc.ErrorCode);
        Assert.DoesNotContain(Pii, rpc.Message, StringComparison.Ordinal);
        Assert.Empty(gate.Pauses);   // -4 per-recipient — НЕ глобальна пауза
    }

    [Fact]
    public void BaseJsonRpcException_MapsToGenericInternal_NoPause_NoPii()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        var mapped = mapper.Map(new JsonRpcException(Err(-1)), CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(RpcErrorSanitizer.InternalErrorCode, (int)rpc.ErrorCode);
        Assert.DoesNotContain(Pii, rpc.Message, StringComparison.Ordinal);
        Assert.Empty(gate.Pauses);
    }

    [Fact]
    public void ClientCancel_PropagatesOriginal_NotWrapped()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var original = new OperationCanceledException(cts.Token);

        var mapped = mapper.Map(original, cts.Token, "sendTextMessage");

        Assert.Same(original, mapped);   // client-cancel → пропускаємо оригінал
    }

    [Fact]
    public void RestartCancel_MapsTo32009_ServiceRestarting()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        // Скасування НЕ від клієнта (token не скасовано) → рестарт демона (W10).
        var mapped = mapper.Map(new OperationCanceledException(), CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(FailPathErrorCodes.ServiceRestarting, (int)rpc.ErrorCode);
    }

    [Theory]
    [MemberData(nameof(RestartClassExceptions))]
    public void StreamBreakExceptions_MapTo32009(Exception ex)
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        var mapped = mapper.Map(ex, CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(FailPathErrorCodes.ServiceRestarting, (int)rpc.ErrorCode);
    }

    public static TheoryData<Exception> RestartClassExceptions() =>
    [
        new IOException("broken pipe"),
        new ObjectDisposedException("stream"),
        new TimeoutException("slow"),
    ];

    [Fact]
    public void UnexpectedException_MapsToGenericInvocationError()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);

        var mapped = mapper.Map(new InvalidOperationException("boom"), CancellationToken.None, "sendTextMessage");

        var rpc = Assert.IsType<RpcErrorException>(mapped);
        Assert.Equal(StreamJsonRpc.Protocol.JsonRpcErrorCode.InvocationError, rpc.ErrorCode);
        Assert.Empty(gate.Pauses);
    }

    [Fact]
    public void AlreadySanitized_RpcErrorException_PassesThrough()
    {
        var gate = new RecordingGate();
        var mapper = NewMapper(gate);
        var original = new RpcErrorException(StreamJsonRpc.Protocol.JsonRpcErrorCode.InvalidParams, "bad");

        var mapped = mapper.Map(original, CancellationToken.None, "sendTextMessage");

        Assert.Same(original, mapped);
    }

    private sealed class RecordingGate : IGlobalPauseGate
    {
        public List<string> Pauses { get; } = [];
        public bool IsPaused { get; private set; }
        public int RetryAfterSeconds { get; private set; }

        public void PauseFor(TimeSpan minimumDuration, string reason)
        {
            Pauses.Add(reason);
            IsPaused = true;
            RetryAfterSeconds = Math.Max(1, (int)minimumDuration.TotalSeconds);
        }
    }
}
