namespace Sankore.Integration.RelayAgent.Tests;

using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Sankore.Integration.RelayAgent.Configuration;
using Sankore.Integration.RelayAgent.Execution;
using Sankore.Integration.RelayAgent.Observability;
using Sankore.Integration.RelayAgent.Protocol;
using Xunit;

/// <summary>
/// The single most important constraint in INT-26: the SQL target is read-only on a DECLARED
/// view, and no part of the statement can come from the wire.
///
/// <para>
/// These tests need no database, and that is itself the point being pinned: every refusal below
/// is decided before a connection is opened, so an order that tries to widen its reach never
/// reaches the IMF's database at all.
/// </para>
/// </summary>
public sealed class SqlViewTargetTests
{
    [Fact]
    public void The_statement_is_built_from_the_file_and_binds_every_value()
    {
        var target = Target();

        var sql = SqlViewOrderExecutor.BuildStatement(
            target,
            new Dictionary<string, string?> { ["reference_compte"] = "CI0012345678" },
            out var bindings);

        sql.Should().Be(
            """SELECT * FROM "reporting"."v_soldes_clients" WHERE "reference_compte" = @p0 LIMIT 201""");

        bindings.Should().ContainSingle();
        bindings[0].Key.Should().Be("p0");
        bindings[0].Value.Should().Be("CI0012345678");
    }

    [Fact]
    public void A_value_that_looks_like_SQL_is_a_value_and_not_SQL()
    {
        // The property the whole design exists for: whatever the wire sends ends up as a bound
        // parameter, so the statement's text is identical to the harmless case above.
        var sql = SqlViewOrderExecutor.BuildStatement(
            Target(),
            new Dictionary<string, string?>
            {
                ["reference_compte"] = "x'; DROP TABLE clients; --",
            },
            out var bindings);

        sql.Should().NotContain("DROP");
        sql.Should().Be(
            """SELECT * FROM "reporting"."v_soldes_clients" WHERE "reference_compte" = @p0 LIMIT 201""");

        bindings[0].Value.Should().Be("x'; DROP TABLE clients; --");
    }

    [Fact]
    public void No_filter_means_no_where_clause_but_the_row_cap_stays()
    {
        var sql = SqlViewOrderExecutor.BuildStatement(
            Target(), new Dictionary<string, string?>(), out var bindings);

        sql.Should().Be("""SELECT * FROM "reporting"."v_soldes_clients" LIMIT 201""");
        bindings.Should().BeEmpty();
    }

    [Fact]
    public async Task An_undeclared_view_name_is_refused_without_touching_a_database()
    {
        var executor = new SqlViewOrderExecutor(Options());

        var result = await executor.ExecuteAsync(
            Order("une-autre-vue", new Dictionary<string, string?>()), default);

        result.Outcome.Should().Be(RelayOutcome.Refused);
        result.ErrorCode.Should().Be(RelayErrorCodes.TargetNotDeclared);
    }

    [Fact]
    public async Task An_undeclared_filter_column_is_refused_without_touching_a_database()
    {
        // Refused and not ignored: ignoring it would silently widen the result set, and the
        // platform would believe it had asked for one customer.
        var executor = new SqlViewOrderExecutor(Options());

        var result = await executor.ExecuteAsync(
            Order("soldes", new Dictionary<string, string?> { ["solde"] = "0" }), default);

        result.Outcome.Should().Be(RelayOutcome.Refused);
        result.ErrorCode.Should().Be(RelayErrorCodes.ParameterNotDeclared);
    }

    [Fact]
    public void The_validator_refuses_a_view_name_that_is_not_a_bare_identifier()
    {
        // This check is what licenses BuildStatement to interpolate at all. If it ever stops
        // firing, the statement builder becomes an injection point via the configuration file.
        var options = new RelayAgentOptions();
        options.Sankore.ChannelUri = "wss://example.test/relay";
        options.Certificate.Pkcs12Path = "/etc/sankore/agent.pfx";

        var bad = new RelaySqlViewTargetOptions
        {
            Name = "soldes",
            ConnectionString = "Host=db",
            Schema = "reporting",
            View = "v_soldes\"; DROP TABLE clients; --",
        };

        options.SqlViewTargets.Add(bad);

        var result = new RelayAgentOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("must be a bare SQL identifier",
            StringComparison.Ordinal));
    }

    private static RelaySqlViewTargetOptions Target()
    {
        var target = new RelaySqlViewTargetOptions
        {
            Name = "soldes",
            ConnectionString = "Host=reporting.lan;Database=cbs;Username=ro;Password=x",
            Schema = "reporting",
            View = "v_soldes_clients",
            MaxRows = 200,
        };

        target.Parameters.Add("reference_compte");
        return target;
    }

    private static IOptions<RelayAgentOptions> Options()
    {
        var options = new RelayAgentOptions();
        options.SqlViewTargets.Add(Target());
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private static RelayOrder Order(string target, IReadOnlyDictionary<string, string?> parameters)
        => new(
            CorrelationId: "corr-1",
            Kind: RelayOrderKind.SqlView,
            Target: target,
            Body: JsonSerializer.SerializeToElement(
                new RelaySqlViewBody(parameters), RelayProtocolJson.Options),
            TimeoutSeconds: null);
}
