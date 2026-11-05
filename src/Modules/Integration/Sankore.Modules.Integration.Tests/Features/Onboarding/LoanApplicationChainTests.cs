namespace Sankore.Modules.Integration.Tests.Features.Onboarding;

using FluentAssertions;
using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// A credit file's application, against the in-memory core banking double (2026-11-05): once per
/// project, and two loans for one client are not one conflict.
/// </summary>
public sealed class LoanApplicationChainTests
{
    private static readonly Guid MessageId = new("dddddddd-0000-0000-0000-000000000009");

    private static LoanApplicationRequest Request(Guid clientId, Guid projectId, decimal amount = 500_000m) =>
        new(projectId, clientId, FakeAdapter.SeededProductCode, amount, "XOF", 24, "Fonds de roulement");

    private static async Task<OnboardingTestContext> WithCustomerAtTheCoreBankingAsync()
    {
        var context = OnboardingTestContext.WithActiveConnection();
        await context.ConsumeKycValidatedAsync(MessageId);
        await context.ExecutePendingCommandsAsync();
        return context;
    }

    [Fact]
    public async Task The_application_reaches_the_core_banking_and_the_status_says_so()
    {
        using var context = await WithCustomerAtTheCoreBankingAsync();
        var project = Guid.NewGuid();

        (await context.LoanGatewayAsync(g => g.GetLoanApplicationStatusAsync(project, default)))
            .State.Should().Be(LoanApplicationState.NotRequested);

        await context.RequestLoanAsync(Request(context.CrmCustomerId, project));
        (await context.LoanGatewayAsync(g => g.GetLoanApplicationStatusAsync(project, default)))
            .State.Should().Be(LoanApplicationState.InProgress);

        await context.ExecutePendingCommandsAsync();

        context.Adapter.LastLoanApplication!.Amount.Should().Be(500_000m);
        context.Adapter.LastLoanApplication.CrmCustomerId.Should().Be(context.CrmCustomerId);

        var status = await context.LoanGatewayAsync(g => g.GetLoanApplicationStatusAsync(project, default));
        status.State.Should().Be(LoanApplicationState.Accepted);
        status.Reference.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Two_requests_for_one_project_queue_one_command()
    {
        using var context = await WithCustomerAtTheCoreBankingAsync();
        var project = Guid.NewGuid();

        await context.RequestLoanAsync(Request(context.CrmCustomerId, project));
        await context.RequestLoanAsync(Request(context.CrmCustomerId, project, amount: 700_000m));

        (await context.CommandsAsync()).Should().ContainSingle(c => c.CommandType == CommandType.SubmitLoanApplication);
    }

    [Fact]
    public async Task A_second_loan_for_the_same_client_is_not_a_reference_conflict()
    {
        // Keyed on the client, the second accepted loan was refused for contradicting the first reference.
        using var context = await WithCustomerAtTheCoreBankingAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await context.RequestLoanAsync(Request(context.CrmCustomerId, first));
        await context.ExecutePendingCommandsAsync();
        await context.RequestLoanAsync(Request(context.CrmCustomerId, second));
        await context.ExecutePendingCommandsAsync();

        (await context.LoanGatewayAsync(g => g.GetLoanApplicationStatusAsync(second, default)))
            .State.Should().Be(LoanApplicationState.Accepted);
    }

    [Fact]
    public async Task Without_a_connection_the_status_says_so()
    {
        using var context = OnboardingTestContext.WithoutConnection();

        (await context.LoanGatewayAsync(g => g.GetLoanApplicationStatusAsync(Guid.NewGuid(), default)))
            .State.Should().Be(LoanApplicationState.NoConnection);
    }
}
