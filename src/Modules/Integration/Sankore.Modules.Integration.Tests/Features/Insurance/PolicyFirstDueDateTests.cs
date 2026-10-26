namespace Sankore.Modules.Integration.Tests.Features.Insurance;

using FluentAssertions;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// L'échéance qu'une police vient d'émettre affiche avant que la synchronisation de l'assureur ne
/// la remplace : « — » sur une police mensuelle laissait l'agent sans réponse au client.
/// </summary>
public sealed class PolicyFirstDueDateTests
{
    private static readonly DateOnly Effective = new(2026, 12, 1);

    [Theory]
    [InlineData(PremiumPeriodicity.Monthly, 2027, 1, 1)]
    [InlineData(PremiumPeriodicity.Quarterly, 2027, 3, 1)]
    [InlineData(PremiumPeriodicity.SemiAnnual, 2027, 6, 1)]
    [InlineData(PremiumPeriodicity.Annual, 2027, 12, 1)]
    public void A_first_premium_already_collected_covers_one_period(PremiumPeriodicity periodicity, int y, int m, int d)
        => PolicyRecord.FirstDueDate(Effective, periodicity, firstPremiumPaid: true).Should().Be(new DateOnly(y, m, d));

    [Fact]
    public void A_premium_still_owed_is_due_at_the_effective_date()
        => PolicyRecord.FirstDueDate(Effective, PremiumPeriodicity.Monthly, firstPremiumPaid: false).Should().Be(Effective);

    [Fact]
    public void A_single_premium_has_no_next_due_date()
        => PolicyRecord.FirstDueDate(Effective, PremiumPeriodicity.Single, firstPremiumPaid: true).Should().BeNull();
}
