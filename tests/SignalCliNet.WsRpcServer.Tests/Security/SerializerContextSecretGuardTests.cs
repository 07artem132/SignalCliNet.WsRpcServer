using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SignalCliNet.WsRpcServer.Serialization;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// A1-guard (task 3.4, W23-стиль): жодна СЕРІАЛІЗОВНА властивість типів, зареєстрованих у
/// <see cref="SignalCliSerializerContext"/>, не сміє нести секрет у імені (token/secret/signature/
/// pepper/privatekey) без явного <c>[JsonIgnore]</c> — інакше плейнтекст витік би на дріт через source-gen.
/// Перевіряємо через <see cref="JsonTypeInfo.Properties"/> (resolve через <c>Default</c>): у цей список
/// потрапляють лише реально серіалізовні властивості (ігноровані [JsonIgnore] відсутні).
/// </summary>
public class SerializerContextSecretGuardTests
{
    private static readonly string[] ForbiddenSubstrings =
        ["token", "secret", "signature", "pepper", "privatekey"];

    [Fact]
    public void RegisteredTypes_HaveNoSerializedSecretProperties()
    {
        var violations = new List<string>();

        foreach (var type in RegisteredRootTypes())
        {
            var info = SignalCliSerializerContext.Default.GetTypeInfo(type);
            if (info is null)
                continue;

            foreach (var name in ForbiddenSerializedProperties(info))
                violations.Add($"{type.Name}.{name}");
        }

        Assert.True(
            violations.Count == 0,
            "Серіалізовні секрет-властивості без [JsonIgnore] у source-gen контексті: " +
            string.Join(", ", violations));
    }

    [Fact]
    public void Guard_Positive_CatchesUnignoredSecretProperty()
    {
        // Негативний контроль: тип із секрет-властивістю без [JsonIgnore] МУСИТЬ ловитися.
        var info = BuildTypeInfo(typeof(LeakyDto));
        Assert.NotEmpty(ForbiddenSerializedProperties(info));
    }

    [Fact]
    public void Guard_Negative_SkipsProperlyIgnoredSecret()
    {
        // [JsonIgnore] на секреті → властивість не серіалізується → не порушення.
        var info = BuildTypeInfo(typeof(SafeDto));
        Assert.Empty(ForbiddenSerializedProperties(info));
    }

    // Реально СЕРІАЛІЗОВНІ секрет-властивості: ім'я містить заборонену підстроку І властивість дійсно
    // пишеться. Під reflection-резолвером [JsonIgnore]-властивість лишається у Properties, але з Get==null
    // (не серіалізується) — тому фільтруємо по Get, щоб предикат збігався з source-gen-семантикою.
    private static IReadOnlyList<string> ForbiddenSerializedProperties(JsonTypeInfo info) =>
        info.Properties
            .Where(p => p.Get is not null &&
                        ForbiddenSubstrings.Any(s => p.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();

    // Типи, зареєстровані у контексті — читаємо через згенеровані властивості JsonTypeInfo<T>.
    private static IEnumerable<Type> RegisteredRootTypes() =>
        typeof(SignalCliSerializerContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
            .Select(t => t.GetGenericArguments()[0]);

    private static JsonTypeInfo BuildTypeInfo(Type type) =>
        new DefaultJsonTypeInfoResolver().GetTypeInfo(type, new JsonSerializerOptions())!;

    // ---- Тестові DTO для позитивного/негативного контролю guard-логіки ----

    private sealed class LeakyDto
    {
        public string AccessToken { get; set; } = "";   // секрет БЕЗ [JsonIgnore] → порушення
        public string Name { get; set; } = "";
    }

    private sealed class SafeDto
    {
        [JsonIgnore] public string DeviceSecret { get; set; } = "";
        public string Name { get; set; } = "";
    }
}
