namespace Sankore.Modules.Integration.Tests.Infrastructure.Configurations;

using System.Text.Json;
using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Infrastructure.Configurations;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// The jsonb converter that stores a connection's settings, and the comparer EF needs beside it.
///
/// <para>
/// <b>Why this is the most consequential of the three <c>$kind</c> switches.</b> The host's wire
/// converter decides what a client may send, and a gap there surfaces as a 422 on the request that
/// introduced it — annoying, immediate, diagnosable. This one decides what the DATABASE holds. A
/// kind it cannot resolve is written happily and read back as <c>null</c>, so the connection
/// silently loses its coordinates: the row is still there, the screen shows a connection, and
/// every call fails somewhere else entirely. Degrading to null rather than throwing is the right
/// choice — the converter's own comment explains it, a row written by a newer version must not
/// break a rollback — and it is exactly what makes a missing switch entry invisible.
/// </para>
///
/// <para>
/// So every case here is driven off <see cref="IntegrationKind"/>: adding a value fails these
/// tests until someone teaches the switch, which is the only moment at which the omission is
/// cheap to fix.
/// </para>
/// </summary>
public sealed class ConnectionSettingsConverterTests
{
    private static readonly ConnectionSettingsConverter Converter = new();

    private static string? ToProvider(ConnectionSettings? settings)
        => (string?)Converter.ConvertToProvider(settings);

    private static ConnectionSettings? FromProvider(string? json)
        => (ConnectionSettings?)Converter.ConvertFromProvider(json);

    public static TheoryData<IntegrationKind> EveryKind()
    {
        var data = new TheoryData<IntegrationKind>();
        foreach (var kind in Enum.GetValues<IntegrationKind>()) data.Add(kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void Every_kind_survives_a_write_and_a_read(IntegrationKind kind)
    {
        var settings = Instantiate(kind);

        var back = FromProvider(ToProvider(settings));

        // The concrete type, because that is what degrades silently: a null answer here is a
        // connection whose settings vanished between a save and the next load.
        back.Should().NotBeNull($"a stored {kind} connection must be readable back");
        back!.GetType().Should().Be(settings.GetType());
        back.ExpectedKind.Should().Be(kind);
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void What_is_written_is_valid_json_carrying_the_discriminator(IntegrationKind kind)
    {
        var json = ToProvider(Instantiate(kind));

        // Parsed, not pattern-matched. The writer builds its output by string surgery —
        // `{"$kind":"X",` + the serialised body with its leading brace trimmed — which produces
        // `{"$kind":"X",}` for a record that serialises to `{}`. Unreachable today because the
        // base record always emits schemaVersion, and that is precisely the kind of "unreachable"
        // that a later [JsonIgnore] makes reachable. A parse failure throws here instead of
        // landing in a jsonb column.
        var act = () => JsonDocument.Parse(json!);
        act.Should().NotThrow();

        using var doc = JsonDocument.Parse(json!);
        doc.RootElement.TryGetProperty("$kind", out var written).Should().BeTrue();
        written.GetString().Should().Be(kind.ToString());
    }

    [Fact]
    public void A_batch_kinds_own_fields_survive_both_levels_of_the_hierarchy()
    {
        // The round trips above would pass on a converter that wrote only the base record's knobs:
        // right type, every vendor field gone. That failure mode is why the converter serialises
        // against the concrete type, so one case asserts it on a kind whose fields live across two
        // levels — the base, BatchCapableSettings, and the leaf.
        ConnectionSettings settings = new PerfectVisionSettings
        {
            RateLimitPerMinute = 90,          // ConnectionSettings
            CutOffTime = new TimeOnly(20, 15), // BatchCapableSettings
            FileEncoding = "ISO-8859-1",
            SftpHost = "sftp.example.ci",
            SftpPort = 2222,
            BalanceViewName = "v_sankore_balances", // PerfectVisionSettings
        };

        var back = FromProvider(ToProvider(settings)) as PerfectVisionSettings;

        back.Should().NotBeNull();
        back!.RateLimitPerMinute.Should().Be(90);
        back.CutOffTime.Should().Be(new TimeOnly(20, 15));
        back.FileEncoding.Should().Be("ISO-8859-1");
        back.SftpHost.Should().Be("sftp.example.ci");
        back.SftpPort.Should().Be(2222);
        back.BalanceViewName.Should().Be("v_sankore_balances");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("""{"baseUrl":"https://cbs.example.ci"}""")]
    [InlineData("""{"$kind":"Mambu","baseUrl":"https://cbs.example.ci"}""")]
    public void A_row_whose_kind_cannot_be_resolved_reads_as_null_rather_than_throwing(string? json)
    {
        // The documented contract, pinned because it is load-bearing for a ROLLBACK: a row written
        // by a newer deployment that knows a kind this one does not must not make the old binary
        // throw while loading a page of connections. Null is a connection the UI reports as
        // unconfigured; an exception is a 500 on a list.
        FromProvider(json).Should().BeNull();
    }

    [Fact]
    public void Null_settings_make_a_null_column_and_not_the_string_null()
    {
        // `"null"` in a jsonb column is a JSON null VALUE, which is not SQL NULL — the difference
        // shows up in `WHERE settings IS NULL` and in every COALESCE over it.
        ToProvider(null).Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void The_comparer_sees_a_replaced_settings_object(IntegrationKind kind)
    {
        // Without this comparer EF's change tracker never notices that a converted reference
        // property was swapped: PUT /connections/{id} answers 200 and the row keeps its old
        // coordinates. That is the bug the comparer exists to prevent, so the test states it on
        // every kind rather than on a convenient one.
        var comparer = new ConnectionSettingsComparer();

        var original = Instantiate(kind);
        var sameAgain = Instantiate(kind);

        comparer.Equals(original, sameAgain).Should().BeTrue(
            "two settings objects with the same content are the same settings");
        comparer.GetHashCode(original).Should().Be(comparer.GetHashCode(sameAgain));

        var changed = original with { RateLimitPerMinute = original.RateLimitPerMinute + 7 };

        comparer.Equals(original, changed).Should().BeFalse(
            $"a {kind} connection whose rate limit was edited must be saved");
    }

    [Fact]
    public void The_comparer_handles_nulls_on_either_side()
    {
        var comparer = new ConnectionSettingsComparer();

        comparer.Equals(null, null).Should().BeTrue();
        comparer.Equals(new FakeSettings(), null).Should().BeFalse();
        comparer.Equals(null, new FakeSettings()).Should().BeFalse();
        comparer.GetHashCode(null).Should().Be(0);
    }

    [Fact]
    public void Two_different_kinds_are_never_equivalent()
    {
        // Both are BatchCapableSettings with every inherited default, so a comparer that read only
        // the base record's properties would call them equal — and an administrator switching a
        // connection from one vendor to the other would get a 200 and no change.
        var comparer = new ConnectionSettingsComparer();

        comparer.Equals(new PerfectVisionSettings(), new OrassSettings()).Should().BeFalse();
    }

    private static ConnectionSettings Instantiate(IntegrationKind kind)
    {
        var type = typeof(ConnectionSettings).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && typeof(ConnectionSettings).IsAssignableFrom(t))
            .Single(t => ((ConnectionSettings)Activator.CreateInstance(t)!).ExpectedKind == kind);

        return (ConnectionSettings)Activator.CreateInstance(type)!;
    }
}
