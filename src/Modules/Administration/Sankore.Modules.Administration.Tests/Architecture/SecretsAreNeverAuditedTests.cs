namespace Sankore.Modules.Administration.Tests.Architecture;

using System.Reflection;
using FluentAssertions;
using MediatR;
using Sankore.Modules.Administration.Features.Users.ChangePassword;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// AuditBehavior sérialise chaque commande dans le journal. Une propriété de mot de passe ou de
/// jeton sans <see cref="SensitiveDataAttribute"/> y part en clair — c'était le cas de la
/// réinitialisation par un administrateur, du changement de mot de passe et de l'activation.
/// </summary>
public sealed class SecretsAreNeverAuditedTests
{
    private static readonly Assembly Module = typeof(ChangePasswordCommand).Assembly;

    private static bool LooksSecret(PropertyInfo p)
        => p.PropertyType == typeof(string)
           && (p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
               || p.Name.Equals("Token", StringComparison.OrdinalIgnoreCase)
               || p.Name.Equals("Secret", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<Type> Requests()
        => Module.GetTypes().Where(t => !t.IsAbstract && t.GetInterfaces().Any(i =>
            i == typeof(IBaseRequest) || i == typeof(ICommand)));

    [Fact]
    public void Every_secret_carried_by_a_request_is_redacted()
    {
        var leaks = Requests()
            .SelectMany(t => t.GetProperties().Where(LooksSecret).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.GetCustomAttribute<SensitiveDataAttribute>(inherit: true) is null)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        leaks.Should().BeEmpty("ces valeurs seraient écrites en clair dans le journal d'audit");
    }

    [Fact]
    public void The_rule_actually_inspects_password_commands()
        // NetArchTest-style garde-fou : une sélection vide rendrait la règle verte sans rien vérifier.
        => Requests().Should().Contain(typeof(ChangePasswordCommand));
}
