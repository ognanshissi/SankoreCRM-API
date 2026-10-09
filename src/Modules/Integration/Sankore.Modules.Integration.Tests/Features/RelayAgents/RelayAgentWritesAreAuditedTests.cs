namespace Sankore.Modules.Integration.Tests.Features.RelayAgents;

using System.Reflection;
using FluentAssertions;
using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-27 criterion 4: <b>all actions are audited</b>.
///
/// <para>
/// The mechanism is a marker interface, and a marker interface is the easiest thing in the
/// codebase to forget: a command that does not implement <see cref="ICommand"/> compiles, runs,
/// mutates state and returns the right answer. It is simply not transacted and not audited — and
/// nothing anywhere says so. The first person to notice is whoever is asked, months later, who
/// revoked an agent the night the batches stopped.
/// </para>
///
/// <para>
/// So the rule is asserted over the types rather than reviewed per pull request, the way
/// <c>HumanActionsAreAuditedTests</c> does for the module as a whole. This suite adds the part
/// that one cannot express: that every write of THIS area also carries
/// <see cref="IResourceCommand"/> with the resource type an auditor would filter on.
/// </para>
/// </summary>
public sealed class RelayAgentWritesAreAuditedTests
{
    /// <summary>
    /// The resource type every write of this area declares. One literal, so a later slice that
    /// invents "RelayAgent" instead of "IntegrationRelayAgent" splits the audit trail in two and
    /// fails here rather than six months later in front of a regulator.
    /// </summary>
    private const string ResourceType = "IntegrationRelayAgent";

    private static readonly Assembly ModuleAssembly =
        typeof(Sankore.Modules.Integration.IntegrationModule).Assembly;

    public static TheoryData<Type> WriteCommands()
    {
        var data = new TheoryData<Type>();
        foreach (var type in Commands()) data.Add(type);
        return data;
    }

    private static List<Type> Commands() =>
        ModuleAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Command", StringComparison.Ordinal)
                        && typeof(IBaseRequest).IsAssignableFrom(t)
                        && t.Namespace?.StartsWith(
                            "Sankore.Modules.Integration.Features.RelayAgents",
                            StringComparison.Ordinal) == true)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_area_has_the_four_write_commands_INT_27_calls_for()
    {
        // Without this the two theories below would pass vacuously if the slices were renamed or
        // moved — the one failure mode a convention test has that the convention does not.
        Commands().Select(t => t.Name).Should().Equal(
            "ExchangeEnrolmentTokenCommand",
            "RecordRelayHeartbeatCommand",
            "RegisterRelayAgentCommand",
            "RevokeRelayAgentCommand");
    }

    [Theory]
    [MemberData(nameof(WriteCommands))]
    public void Every_write_implements_the_audit_marker(Type command)
    {
        typeof(ICommand).IsAssignableFrom(command).Should().BeTrue(
            "{0} mutates state; ICommand is what activates TransactionBehavior and AuditBehavior, "
            + "and without it the write happens silently outside both",
            command.Name);
    }

    [Theory]
    [MemberData(nameof(WriteCommands))]
    public void Every_write_names_the_resource_it_touches(Type command)
    {
        typeof(IResourceCommand).IsAssignableFrom(command).Should().BeTrue(
            "{0} must be findable in audit.entries by the entity it concerns", command.Name);

        // Read off an instance rather than off the type, because ResourceType is an expression-
        // bodied property: only an instance can be asked what it actually answers.
        var instance = (IResourceCommand)InstantiateWithDefaults(command);

        instance.ResourceType.Should().Be(ResourceType);
    }

    /// <summary>
    /// Builds a command with default arguments. Every one of them is a record whose constructor
    /// takes strings, Guids and nullable ints, so defaults are enough — nothing here runs, the
    /// instance exists only to be asked for its resource type.
    /// </summary>
    private static object InstantiateWithDefaults(Type command)
    {
        var ctor = command.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

        var args = ctor.GetParameters()
            .Select(p => p.ParameterType == typeof(string)
                ? (object?)string.Empty
                : p.ParameterType.IsValueType
                    ? Activator.CreateInstance(p.ParameterType)
                    : null)
            .ToArray();

        return ctor.Invoke(args);
    }

    [Fact]
    public void A_write_carrying_a_token_or_a_thumbprint_marks_it_as_sensitive_data()
    {
        // audit.entries is append-only and kept for years, and AuditBehavior serialises EVERY
        // property of every command into it. The enrolment token is a bearer credential; the
        // thumbprint is not a secret but it is the identifier the agent channel authenticates on.
        // SanitizedJsonSerializer replaces a marked property with "***" and keeps its name, so the
        // row still records which sensitive fields a command carried.
        var offenders = new List<string>();

        foreach (var command in Commands())
        {
            foreach (var property in command.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var sensitive = property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                                || property.Name.Contains("Thumbprint", StringComparison.OrdinalIgnoreCase);

                if (!sensitive) continue;

                // inherit: true — a record's positional property carries the attribute through
                // [property: SensitiveData] on the primary constructor parameter.
                var marked = property
                    .GetCustomAttributes(typeof(SensitiveDataAttribute), inherit: true)
                    .Length > 0;

                if (!marked) offenders.Add($"{command.Name}.{property.Name}");
            }
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void The_marker_and_the_attribute_are_the_types_this_test_believes_they_are()
    {
        typeof(ICommand).FullName.Should().Be("Sankore.Shared.Infrastructure.Behaviors.ICommand");
        typeof(IResourceCommand).FullName.Should().Be("Sankore.Shared.Kernel.IResourceCommand");
        typeof(SensitiveDataAttribute).FullName.Should().Be("Sankore.Shared.Kernel.SensitiveDataAttribute");
        ModuleAssembly.GetName().Name.Should().Be("Sankore.Modules.Integration");
    }
}
