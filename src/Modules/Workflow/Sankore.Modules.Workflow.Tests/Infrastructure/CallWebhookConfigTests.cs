namespace Sankore.Modules.Workflow.Tests.Infrastructure;

using FluentAssertions;
using Microsoft.Extensions.Options;
using Sankore.Modules.Workflow.Domain;
using Sankore.Modules.Workflow.Features.Templates.Transitions.AddAction;
using Sankore.Modules.Workflow.Infrastructure.Actions.Executors;
using Sankore.Shared.Infrastructure.Egress;
using Xunit;

/// <summary>
/// The « Appeler un webhook » action used to be a plain HttpClient on whatever URL, method and
/// headers a template carried. Its configuration is now refused at save, and again at execution
/// for actions saved before.
/// </summary>
public sealed class CallWebhookConfigTests
{
    [Fact]
    public void A_public_https_post_with_ordinary_headers_is_accepted()
        => Config("""{ "url": "https://partner.example.com/hook", "method": "post", "headers": { "X-Api-Key": "abc" } }""")
            .Validate().Should().BeNull();

    [Theory]
    [InlineData("""{ "url": "http://partner.example.com/hook" }""")]
    [InlineData("""{ "url": "https://169.254.169.254/latest/meta-data" }""")]
    [InlineData("""{ "url": "https://localhost/" }""")]
    [InlineData("""{ "url": "https://partner.example.com:5432/" }""")]
    [InlineData("""{ "url": "https://partner.example.com/hook", "method": "DELETE" }""")]
    [InlineData("""{ "url": "https://partner.example.com/hook", "method": "GET" }""")]
    [InlineData("""{ "url": "https://partner.example.com/hook", "headers": { "Host": "sankore-admin" } }""")]
    [InlineData("""{ "url": "https://partner.example.com/hook", "headers": { "Content-Length": "0" } }""")]
    [InlineData("""{ "url": "https://partner.example.com/hook", "headers": { "X-A": "b\r\nX-Injected: 1" } }""")]
    [InlineData("""{ "url": "https://partner.example.com/hook", "headers": { "Bad Name": "x" } }""")]
    public void A_dangerous_configuration_is_refused(string json)
        => Config(json).Validate().Should().NotBeNullOrWhiteSpace();

    [Fact]
    public async Task The_validator_refuses_it_when_the_action_is_saved()
    {
        var validator = new AddActionValidator(Options.Create(new OutboundHttpOptions()));

        var result = await validator.ValidateAsync(new AddActionCommand(
            Guid.NewGuid(), Guid.NewGuid(), ActionType.CallWebhook, """{ "url": "http://10.0.0.5/" }"""));

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Another_action_type_is_not_read_as_a_webhook()
    {
        var validator = new AddActionValidator(Options.Create(new OutboundHttpOptions()));

        var result = await validator.ValidateAsync(new AddActionCommand(
            Guid.NewGuid(), Guid.NewGuid(), ActionType.AssignUser, """{ "userId": "x" }"""));

        result.IsValid.Should().BeTrue();
    }

    private static CallWebhookConfig Config(string json)
        => CallWebhookConfig.Parse(json) ?? throw new InvalidOperationException("unparseable test input");
}
