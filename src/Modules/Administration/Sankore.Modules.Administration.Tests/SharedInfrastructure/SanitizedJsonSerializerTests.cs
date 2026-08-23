namespace Sankore.Modules.Administration.Tests.SharedInfrastructure;

using System.Text.Json;
using FluentAssertions;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;

public sealed class SanitizedJsonSerializerTests
{
    // ── fixtures ────────────────────────────────────────────────────────────

    private sealed record PlainCommand(string Username, string Email);

    private sealed record SensitiveCommand(
        string Username,
        [property: SensitiveData] string Password);

    private sealed record MultiSensitiveCommand(
        string Email,
        [property: SensitiveData] string Password,
        [property: SensitiveData] string ConfirmPassword,
        string FirstName);

    private sealed record NestedCommand(
        string Name,
        SensitiveCommand Inner);

    // ── S1 : propriétés non-sensibles conservées ─────────────────────────

    [Fact]
    public void Non_sensitive_properties_are_serialized_normally()
    {
        var cmd = new PlainCommand("alice", "alice@sankore.sn");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Username").GetString().Should().Be("alice");
        doc.RootElement.GetProperty("Email").GetString().Should().Be("alice@sankore.sn");
    }

    // ── S2 : propriété sensible remplacée par *** ──────────────────────

    [Fact]
    public void Sensitive_property_is_replaced_by_redaction_marker()
    {
        var cmd = new SensitiveCommand("alice", "SuperSecret123!");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Password").GetString().Should().Be("***");
    }

    // ── S3 : propriété non-sensible adjacente conservée ──────────────────

    [Fact]
    public void Non_sensitive_property_next_to_sensitive_one_is_preserved()
    {
        var cmd = new SensitiveCommand("bob", "s3cr3t");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Username").GetString().Should().Be("bob");
    }

    // ── S4 : plusieurs propriétés sensibles toutes masquées ──────────────

    [Fact]
    public void Multiple_sensitive_properties_are_all_redacted()
    {
        var cmd = new MultiSensitiveCommand(
            "alice@sankore.sn", "pass1", "pass1", "Alice");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Password").GetString().Should().Be("***");
        doc.RootElement.GetProperty("ConfirmPassword").GetString().Should().Be("***");
        doc.RootElement.GetProperty("Email").GetString().Should().Be("alice@sankore.sn");
        doc.RootElement.GetProperty("FirstName").GetString().Should().Be("Alice");
    }

    // ── S5 : JSON valide produit ──────────────────────────────────────────

    [Fact]
    public void Output_is_valid_json()
    {
        var cmd = new SensitiveCommand("charlie", "p@ssw0rd");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var act = () => JsonDocument.Parse(json);

        act.Should().NotThrow();
    }

    // ── S6 : valeur sensible originale absente du JSON ────────────────────

    [Fact]
    public void Original_sensitive_value_does_not_appear_in_json()
    {
        const string secret = "MyV3ryS3cr3tP@ssw0rd!";
        var cmd = new SensitiveCommand("dave", secret);

        var json = SanitizedJsonSerializer.Serialize(cmd);

        json.Should().NotContain(secret);
        json.Should().Contain("***");
    }

    // ── S7 : objet sans aucune propriété sensible ─────────────────────────

    [Fact]
    public void Object_with_no_sensitive_properties_is_unchanged()
    {
        var cmd = new PlainCommand("eve", "eve@sankore.sn");

        var json = SanitizedJsonSerializer.Serialize(cmd);

        json.Should().NotContain("***");
    }

    // ── S8 : commandes réelles du domaine ────────────────────────────────

    [Fact]
    public void LoginCommand_password_is_redacted()
    {
        var cmd = new Sankore.Modules.Administration.Features.Authentication.Login.LoginCommand(
            "admin@sankore.sn", "AdminPass123!");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Password").GetString().Should().Be("***");
        doc.RootElement.GetProperty("Email").GetString().Should().Be("admin@sankore.sn");
    }

    [Fact]
    public void ResetPasswordCommand_new_password_is_redacted()
    {
        var userId = Guid.NewGuid();
        var cmd = new Sankore.Modules.Administration.Features.Users.ResetPassword.ResetPasswordCommand(
            userId, "NewPass456!");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("NewPassword").GetString().Should().Be("***");
        doc.RootElement.GetProperty("UserId").GetString().Should().Be(userId.ToString());
    }

    [Fact]
    public void RegisterCommand_both_passwords_are_redacted()
    {
        var cmd = new Sankore.Modules.Administration.Features.Users.Register.RegisterCommand(
            "newuser@sankore.sn", "Pass1!", "Pass1!", "Fatou", "Diallo");

        var json = SanitizedJsonSerializer.Serialize(cmd);
        var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Password").GetString().Should().Be("***");
        doc.RootElement.GetProperty("ConfirmPassword").GetString().Should().Be("***");
        doc.RootElement.GetProperty("Email").GetString().Should().Be("newuser@sankore.sn");
        doc.RootElement.GetProperty("FirstName").GetString().Should().Be("Fatou");
    }
}
