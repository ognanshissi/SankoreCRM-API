namespace Sankore.Modules.Integration.Tests.Features.CallLog;

using System.Reflection;
using FluentAssertions;
using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// INT-08 criterion 3: <b>human actions — connecting, mapping, replaying, cancelling — go through
/// the MediatR audit pipeline.</b>
///
/// <para>
/// Nothing in this slice can satisfy that criterion by itself: the connection, mapping, replay and
/// cancel commands belong to INT-03, INT-04 and INT-05. What this slice owes is the thing that
/// makes the criterion stay true as those slices land, one branch at a time, over weeks. The
/// mechanism is a marker interface, and a marker interface is the easiest thing in the codebase to
/// forget: a command that does not implement <see cref="ICommand"/> compiles, runs, mutates state
/// and returns the right answer. It is simply not transacted and not audited — and nothing
/// anywhere says so. The first person to notice is whoever is asked, months later, who cancelled
/// a command.
/// </para>
///
/// <para>
/// So the rule is asserted over the assembly rather than reviewed per pull request, in the
/// reflection style of M01's <c>OpenApiSchemaNameTests</c>: the convention is read off the types
/// that exist, and a new slice that breaks it fails the build of the module it was added to.
/// </para>
///
/// <para>
/// The suite passes vacuously while <c>Features/</c> holds no command — the two slices of this
/// chantier are queries, deliberately: reading a journal of calls is not itself an event worth a
/// second journal. The first command another chantier adds is the first time these tests have
/// anything to say, which is exactly when they are needed.
/// </para>
/// </summary>
public sealed class HumanActionsAreAuditedTests
{
    private static readonly Assembly ModuleAssembly = typeof(IntegrationModule).Assembly;

    /// <summary>
    /// Property names that, on a command of this module, mean "a credential or a business payload
    /// is travelling through the audit pipeline". The vocabulary is the module's own: INT-03 stores
    /// per-tenant CBS credentials through the vault, and INT-05 carries an encrypted payload — and
    /// <see cref="AuditBehavior{TRequest,TResponse}"/> serialises <i>every</i> property of every
    /// command into <c>audit.entries</c>, which is append-only and kept for years.
    /// </summary>
    private static readonly string[] SensitivePropertyNames =
        ["Payload", "Value", "Secret", "Credential", "Token"];

    /// <summary>
    /// MediatR requests under <c>Features/</c> whose name ends in <c>Command</c>.
    ///
    /// <para>
    /// The <see cref="IBaseRequest"/> condition is not redundant with the name. This module's
    /// vocabulary is full of nouns that end in "Command" without being one —
    /// <c>integration_command</c> is a table, so a read DTO called <c>PendingCommand</c> or a
    /// projection called <c>RejectedCommand</c> is an entirely reasonable name for a type that
    /// must NOT be audited because it is not a request at all. Matching on the name alone would
    /// fail another chantier's build for a type that is doing nothing wrong.
    /// </para>
    /// </summary>
    private static IEnumerable<Type> FeatureCommands() =>
        ModuleAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && t.Name.EndsWith("Command", StringComparison.Ordinal)
                        && typeof(IBaseRequest).IsAssignableFrom(t)
                        && t.Namespace?.Contains(".Features.", StringComparison.Ordinal) == true);

    [Fact]
    public void Every_command_under_Features_implements_the_ICommand_marker()
    {
        var offenders = FeatureCommands()
            .Where(t => !typeof(ICommand).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "ICommand is what activates TransactionBehavior and AuditBehavior. A command without "
            + "it still works — it is simply neither transacted nor audited, silently, and that is "
            + "the whole of criterion 3: a replay or a cancel nobody can attribute afterwards");
    }

    [Fact]
    public void A_command_carrying_a_payload_or_a_credential_marks_it_as_sensitive_data()
    {
        var offenders = new List<string>();

        foreach (var command in FeatureCommands())
        {
            foreach (var property in command.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!SensitivePropertyNames.Contains(property.Name, StringComparer.Ordinal)) continue;

                // inherit: true — a record's positional property can carry the attribute through
                // [property: SensitiveData] on the primary constructor parameter, which is the
                // idiom the rest of the repo uses.
                var marked = property
                    .GetCustomAttributes(typeof(SensitiveDataAttribute), inherit: true)
                    .Length > 0;

                if (!marked) offenders.Add($"{command.FullName}.{property.Name}");
            }
        }

        offenders.Should().BeEmpty(
            "SanitizedJsonSerializer replaces a marked property with \"***\" and keeps its NAME, "
            + "so the audit row still records which sensitive fields a command carried. Without "
            + "the attribute the value itself is written verbatim into audit.entries — which is "
            + "append-only, so there is no second chance to redact it");
    }

    [Fact]
    public void The_marker_and_the_attribute_are_the_types_this_test_believes_they_are()
    {
        // A rename or a move of either would make both tests above pass by finding nothing, which
        // is the one failure mode a convention test has that the convention itself does not.
        typeof(ICommand).FullName.Should().Be("Sankore.Shared.Infrastructure.Behaviors.ICommand");
        typeof(SensitiveDataAttribute).FullName.Should().Be("Sankore.Shared.Kernel.SensitiveDataAttribute");

        // And that the scan is pointed at this module's assembly rather than at the test one,
        // which is the other way both assertions above could pass by finding nothing.
        ModuleAssembly.GetName().Name.Should().Be("Sankore.Modules.Integration");
        ModuleAssembly.GetTypes().Should().Contain(
            typeof(Sankore.Modules.Integration.Features.CallLog.ListCallLog.ListCallLogQuery));
    }
}
