namespace Sankore.Api.Tests.Infrastructure;

using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Sankore.Api.Infrastructure;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The host's wire converter for the Integration module's polymorphic connection settings.
///
/// <para>
/// <b>Why this suite exists, and why it is driven off the enum.</b> Adding an
/// <see cref="IntegrationKind"/> means teaching three places independently: this converter (what a
/// client may send), the module's <c>ConnectionSettingsConverter</c> (what the database stores),
/// and the settings record itself. The converter's own doc comment calls that duplication a known
/// wart and argues — correctly — that two literal, adjacent switches beat a reflection scan that
/// fails at run time on a renamed record. What was missing is the thing that notices when one of
/// them is not taught: every case here enumerates <c>IntegrationKind</c>, so a new value fails
/// these tests until someone handles it, rather than reaching a tenant as a 422 on a field they
/// filled in correctly.
/// </para>
///
/// <para>
/// The private <c>ResolveType</c> switch is never touched. Every assertion goes through
/// <c>Read</c> and <c>Write</c>, which is what the HTTP pipeline calls — a test that reached the
/// switch directly would pass while a broken <c>Write</c> emitted a discriminator <c>Read</c>
/// cannot resolve.
/// </para>
/// </summary>
public sealed class ConnectionSettingsJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ConnectionSettingsJsonConverter());
        return options;
    }

    public static TheoryData<IntegrationKind> EveryKind()
    {
        var data = new TheoryData<IntegrationKind>();
        foreach (var kind in Enum.GetValues<IntegrationKind>()) data.Add(kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Every_kind_has_exactly_one_settings_record(IntegrationKind kind)
    {
        // The claim a reader of the enum would assume and nothing stated: each kind is
        // configurable. Two records claiming one kind is just as broken as none — the converter
        // would store whichever the switch happens to name, so the count is asserted, not the
        // existence.
        var records = SettingsRecords().Where(r => ExpectedKindOf(r) == kind).ToList();

        records.Should().HaveCount(
            1, $"{kind} is offered to tenants by ConnectionSettingsValidator's error message");
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Every_kind_round_trips_through_the_wire_converter(IntegrationKind kind)
    {
        var settings = Instantiate(SettingsRecords().Single(r => ExpectedKindOf(r) == kind));

        var json = JsonSerializer.Serialize(settings, Options);
        var back = JsonSerializer.Deserialize<ConnectionSettings>(json, Options);

        // The CONCRETE type, not merely a non-null answer. A switch entry that paired a kind with
        // its neighbour's record — `Amplitude => typeof(SabSettings)`, one line's worth of
        // copy-paste — survives a null check and would silently store one vendor's coordinates
        // under another's name.
        back.Should().NotBeNull($"a {kind} connection must be configurable through the API");
        back!.GetType().Should().Be(settings.GetType());
        back.ExpectedKind.Should().Be(kind);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void The_discriminator_written_is_the_one_the_reader_resolves(IntegrationKind kind)
    {
        var settings = Instantiate(SettingsRecords().Single(r => ExpectedKindOf(r) == kind));

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(settings, Options));

        // Pinned as a value and not only through the round trip: `$kind` is part of the published
        // API shape — a front-end builds it by hand to create a connection — so renaming the
        // property or emitting the enum numerically would break every client while the round trip
        // above kept passing.
        doc.RootElement.TryGetProperty("$kind", out var written).Should().BeTrue();
        written.GetString().Should().Be(kind.ToString());
    }

    [Theory]
    [InlineData("""{"baseUrl":"https://cbs.example.ci"}""")]
    [InlineData("""{"$kind":"Mambu","baseUrl":"https://cbs.example.ci"}""")]
    [InlineData("""{"$kind":null}""")]
    public void An_absent_or_unknown_discriminator_reads_as_null(string json)
    {
        // The documented contract the validators lean on: null settings are refused with a message
        // naming the field. Inferring the kind from the payload's shape — which M13's converter
        // does for lead sources — is wrong here, because several kinds share BatchCapableSettings
        // and are indistinguishable by their properties. A guess would store Amplitude coordinates
        // on a Perfect Vision connection.
        JsonSerializer.Deserialize<ConnectionSettings>(json, Options).Should().BeNull();
    }

    [Fact]
    public void Settings_survive_the_round_trip_with_their_own_fields_intact()
    {
        // The round-trip cases above would pass on a converter that serialised only the base
        // record's knobs: the type is right and every vendor-specific field is gone. That failure
        // mode is why the converter serialises against the concrete type, so it is worth one
        // explicit case — on a batch kind, whose fields live across two levels of the hierarchy.
        // Declared as ConnectionSettings, which is load-bearing: see the test below.
        ConnectionSettings settings = new PerfectVisionSettings
        {
            CutOffTime = new TimeOnly(19, 30),
            FileEncoding = "ISO-8859-1",
            FieldSeparator = "|",
            OutboundDirectory = "/upload/sankore",
            SftpHost = "sftp.example.ci",
            BalanceViewName = "v_sankore_balances",
            RateLimitPerMinute = 120,
        };

        var back = JsonSerializer.Deserialize<ConnectionSettings>(
            JsonSerializer.Serialize(settings, Options), Options) as PerfectVisionSettings;

        back.Should().NotBeNull();
        back!.CutOffTime.Should().Be(new TimeOnly(19, 30));
        back.FileEncoding.Should().Be("ISO-8859-1");
        back.FieldSeparator.Should().Be("|");
        back.OutboundDirectory.Should().Be("/upload/sankore");
        back.SftpHost.Should().Be("sftp.example.ci");
        back.BalanceViewName.Should().Be("v_sankore_balances");
        back.RateLimitPerMinute.Should().Be(120);
    }

    [Fact]
    public void Serialising_a_concrete_settings_instance_bypasses_the_converter()
    {
        // A trap, pinned so the next reader meets it here rather than in a 422 nobody can explain.
        // A JsonConverter<ConnectionSettings> is chosen from the STATIC type, so serialising a
        // variable declared as PerfectVisionSettings writes no `$kind` at all — and the result
        // reads back as null, which the validators report as "settings is required" on a payload
        // that looks complete.
        //
        // Production is safe because the request DTOs declare the property as ConnectionSettings?,
        // and that is exactly the fact worth protecting: this test is what notices if a DTO is
        // ever narrowed to a concrete kind for convenience.
        var concrete = new PerfectVisionSettings { SftpHost = "sftp.example.ci" };

        var viaConcreteType = JsonSerializer.Serialize(concrete, Options);
        viaConcreteType.Should().NotContain("$kind");
        JsonSerializer.Deserialize<ConnectionSettings>(viaConcreteType, Options).Should().BeNull();

        var viaBaseType = JsonSerializer.Serialize<ConnectionSettings>(concrete, Options);
        viaBaseType.Should().Contain("$kind");
        JsonSerializer.Deserialize<ConnectionSettings>(viaBaseType, Options).Should().NotBeNull();
    }

    [Fact]
    public void No_secret_value_is_carried_by_any_settings_record()
    {
        // Settings are returned by GET connections/{id}. Credentials live in M12's vault and the
        // records carry only *references* to them, so a property whose name says it holds a value
        // would publish a tenant's CBS password to anyone who can read its connection. Asserted
        // over every record at once, because the next kind's author will copy a neighbour.
        var offenders = SettingsRecords()
            .SelectMany(r => r.GetProperties())
            .Where(p => IsSuspicious(p.Name))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        offenders.Should().BeEmpty(
            "a credential belongs in the secrets vault; settings may hold only a VaultRef to it");
    }

    private static bool IsSuspicious(string name)
    {
        // Two sanctioned shapes, and the second was found BY this test: "VaultRef" is a pointer
        // into M12's vault, and "...Endpoint" is a URL — TemenosSettings.TokenEndpoint is where
        // OAuth credentials are exchanged, not a credential. Neither is the secret, and flagging
        // either would train the next reader to add exemptions instead of reading the finding.
        if (name.EndsWith("VaultRef", StringComparison.Ordinal)) return false;
        if (name.EndsWith("Endpoint", StringComparison.Ordinal)) return false;

        foreach (var word in new[] { "Password", "Secret", "ApiKey", "PrivateKey", "Token", "Credential" })
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static IEnumerable<Type> SettingsRecords()
        => typeof(ConnectionSettings).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(ConnectionSettings).IsAssignableFrom(t));

    private static ConnectionSettings Instantiate(Type type)
        => (ConnectionSettings)Activator.CreateInstance(type)!;

    private static IntegrationKind ExpectedKindOf(Type type) => Instantiate(type).ExpectedKind;
}
