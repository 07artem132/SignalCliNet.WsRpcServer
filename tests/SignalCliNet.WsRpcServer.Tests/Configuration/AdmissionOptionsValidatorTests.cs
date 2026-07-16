using SignalCliNet.WsRpcServer.Deployment;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить валідацію <see cref="AdmissionOptions"/> (секції 1–2): межі числових параметрів через
/// згенерований <see cref="AdmissionOptionsValidator"/> ([Range]) і крос-правило floor ≤ aggregate,
/// яке AddAdmissionCore навішує через .Validate(...).
/// </summary>
public class AdmissionOptionsValidatorTests
{
    private static AdmissionOptions Valid() => new()
    {
        Enabled = true,
        AggregateBudgetPerWindow = 300,
        BudgetWindowMinutes = 60,
        PerUserFloorPerWindow = 10,
        NewRecipientsPerWindow = 5,
        ReserveBlockSize = 10,
    };

    [Fact]
    public void Defaults_AreValid()
    {
        Assert.True(new AdmissionOptionsValidator().Validate(null, new AdmissionOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100001)]
    public void AggregateBudget_OutOfRange_Fails(int value)
    {
        var options = Valid();
        options.AggregateBudgetPerWindow = value;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void BudgetWindowMinutes_OutOfRange_Fails(int value)
    {
        var options = Valid();
        options.BudgetWindowMinutes = value;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public void PerUserFloor_OutOfRange_Fails(int value)
    {
        var options = Valid();
        options.PerUserFloorPerWindow = value;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void NewRecipientsPerWindow_OutOfRange_Fails(int value)
    {
        var options = Valid();
        options.NewRecipientsPerWindow = value;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void ReserveBlockSize_OutOfRange_Fails(int value)
    {
        var options = Valid();
        options.ReserveBlockSize = value;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(1, 100000)]
    [InlineData(60, 1440)]
    [InlineData(5, 1000)]
    public void Boundaries_AreValid(int window, int aggregate)
    {
        var options = Valid();
        options.BudgetWindowMinutes = window;
        options.AggregateBudgetPerWindow = aggregate;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void PerUserFloor_MayEqualAggregate_ForCrossRule()
    {
        // Крос-правило (floor ≤ aggregate) — окремий .Validate у AddAdmissionCore; [Range]-валідатор
        // самих меж не порушує при floor == aggregate.
        var options = Valid();
        options.PerUserFloorPerWindow = 300;
        options.AggregateBudgetPerWindow = 300;
        Assert.True(new AdmissionOptionsValidator().Validate(null, options).Succeeded);
    }
}
