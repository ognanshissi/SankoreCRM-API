namespace Sankore.Modules.Integration.Tests.Contract;

using FluentAssertions;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// INT-10 / INT-13 / INT-22. What every core banking adapter must do with a history.
///
/// <para>
/// A read-only port, so it inherits the entity facts and not the write ones. The paging fact is
/// the reason this suite exists at all: the monthly-flow ceiling of simplified KYC is computed by
/// walking the history, and a pager that never reports its end sends that job into an endless
/// loop — or, worse, a pager that reports the end too early understates a flow a regulator
/// measures the ceiling against.
/// </para>
/// </summary>
public abstract class CbsTransactionPortContractTests : IntegrationEntityPortContractTests<ICbsTransactionPort>
{
    protected abstract ExternalId ExistingCustomerId { get; }

    protected abstract ExternalId ExistingAccountId { get; }

    /// <summary>A window wide enough to contain whatever the implementation holds.</summary>
    protected virtual DateOnly WindowFrom => new(2000, 1, 1);

    protected virtual DateOnly WindowTo => new(2100, 1, 1);

    /// <summary>A window that predates any deployment, so nothing can fall inside it.</summary>
    protected virtual DateOnly EmptyWindowFrom => new(1900, 1, 1);

    protected virtual DateOnly EmptyWindowTo => new(1900, 12, 31);

    protected virtual YearMonth Month => new(2024, 1);

    protected override async Task<IntegrationResult> CallAgainstAnAbsentEntityAsync(
        ICbsTransactionPort port)
        => await port.GetTransactionsAsync(
            AbsentId, WindowFrom, WindowTo, null, CancellationToken.None);

    protected override async Task<(IntegrationResult Result, Action ReadValue)> AnyValuedCallAsync(
        ICbsTransactionPort port)
    {
        var result = await port.GetTransactionsAsync(
            ExistingAccountId, WindowFrom, WindowTo, null, CancellationToken.None);
        return (result, () => _ = result.Value);
    }

    [Fact]
    public async Task Paging_from_no_cursor_should_terminate_with_a_null_cursor()
    {
        var sut = CreatePort();
        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;

        do
        {
            var page = await sut.GetTransactionsAsync(
                ExistingAccountId, WindowFrom, WindowTo, cursor, CancellationToken.None);

            page.IsSuccess.Should().BeTrue();
            seen.AddRange(page.Value.Items.Select(t => t.Reference));
            cursor = page.Value.NextCursor;
            pages++;

            // A bound, not a convenience: without it a pager that keeps handing back its own
            // cursor hangs the suite instead of failing it, and a hung job is what it would do in
            // production.
            pages.Should().BeLessThan(200, "the pager must advance, not loop");
        }
        while (cursor is not null);

        cursor.Should().BeNull();
        seen.Should().OnlyHaveUniqueItems(
            "a page boundary that re-serves a movement double-counts it in the flow ceiling");
    }

    [Fact]
    public async Task A_window_with_no_movement_should_be_an_empty_page_and_not_a_failure()
    {
        var sut = CreatePort();

        var page = await sut.GetTransactionsAsync(
            ExistingAccountId, EmptyWindowFrom, EmptyWindowTo, null, CancellationToken.None);

        page.IsSuccess.Should().BeTrue("an account with no movement in a window is an answer");
        page.Value.Items.Should().BeEmpty();
        page.Value.NextCursor.Should().BeNull();
    }

    [Fact]
    public async Task The_monthly_flow_of_an_absent_customer_should_be_functional_not_found()
    {
        ShouldBeAbsentEntity(
            await CreatePort().GetMonthlyFlowAsync(AbsentId, Month, CancellationToken.None));
    }

    [Fact]
    public async Task The_monthly_flow_should_total_both_directions()
    {
        var sut = CreatePort();

        var flow = await sut.GetMonthlyFlowAsync(ExistingCustomerId, Month, CancellationToken.None);

        flow.IsSuccess.Should().BeTrue();
        flow.Value.Month.Should().Be(Month, "the answer must name the month it was asked about");
        flow.Value.CreditTotal.Should().BeGreaterThanOrEqualTo(0);
        flow.Value.DebitTotal.Should().BeGreaterThanOrEqualTo(0);

        // INT-22 measures the ceiling against everything that moved, not against the net: a
        // customer who received and spent the same sum has used the account, and netting it to
        // zero is how a simplified file passes a ceiling it should have breached.
        flow.Value.Total.Should().Be(flow.Value.CreditTotal + flow.Value.DebitTotal);
    }
}
