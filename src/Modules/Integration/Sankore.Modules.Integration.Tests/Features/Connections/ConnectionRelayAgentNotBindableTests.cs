namespace Sankore.Modules.Integration.Tests.Features.Connections;

using System.Reflection;
using FluentAssertions;
using Sankore.Modules.Integration.Features.Connections.CreateConnection;
using Sankore.Modules.Integration.Features.Connections.UpdateConnection;
using Xunit;

/// <summary>
/// <c>RelayAgentId</c> must not be settable by a client — on a request DTO or on the command a
/// request binds into (INT-03 / INT-26).
///
/// <para>
/// <b>This guards a cross-tenant leak, in both directions.</b> The relay agent holds the only
/// outbound connection from an IMF's own network and executes orders inside it: local HTTP calls,
/// SFTP deposits and reads, read-only SQL. Nothing in this module can validate the id — relay
/// agent registration is INT-27, which is not built — so a tenant-A administrator who set
/// <c>relayAgentId</c> to tenant B's agent would have A's command payloads (identity documents,
/// addresses, declared income) executed inside B's network, and could read B's SFTP directories
/// and SQL views.
/// </para>
///
/// <para>
/// The field is therefore server-set only, written by INT-27's enrolment flow — the one place
/// that holds the agent's identity and can guarantee the tenant match when it mints the
/// certificate. Re-adding it to a request has to come past this test, which is the point: a
/// validation rule can be written and later weakened, while an absent field cannot be sent at
/// all. Same remedy the repo applies to <c>Lead.LeadSourceConfigId</c>.
/// </para>
/// </summary>
public sealed class ConnectionRelayAgentNotBindableTests
{
    /// <summary>Every type a request body of this area binds into.</summary>
    private static readonly Type[] BindableTypes =
    [
        typeof(CreateConnectionRequest),
        typeof(CreateConnectionCommand),
        typeof(UpdateConnectionRequest),
        typeof(UpdateConnectionCommand),
    ];

    public static TheoryData<Type> ClientWritableTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in BindableTypes) data.Add(type);
        return data;
    }

    [Theory]
    [MemberData(nameof(ClientWritableTypes))]
    public void No_create_or_update_type_exposes_a_relay_agent_id(Type type)
    {
        var offenders = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => name.Contains("RelayAgent", StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty(
            "{0} is populated from a request body; a client-settable relay agent id would let "
            + "one tenant route its writes through another tenant's on-premise network",
            type.Name);
    }

    [Fact]
    public void The_constructors_take_no_relay_agent_either()
    {
        // A record's positional parameters are also how a hand-written binder or a future
        // [AsParameters] shape could reintroduce the field.
        var parameters = BindableTypes
            .SelectMany(type => type.GetConstructors())
            .SelectMany(ctor => ctor.GetParameters())
            .Select(p => p.Name ?? string.Empty)
            .ToList();

        parameters.Should().NotContain(
            name => name.Contains("relayAgent", StringComparison.OrdinalIgnoreCase));
    }
}
