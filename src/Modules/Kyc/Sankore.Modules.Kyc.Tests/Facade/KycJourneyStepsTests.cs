namespace Sankore.Modules.Kyc.Tests.Facade;

using FluentAssertions;
using Sankore.Modules.Kyc;
using Sankore.Modules.Kyc.Domain;
using Xunit;

public sealed class KycJourneyStepsTests
{
    [Theory]
    [InlineData(KycFileStatus.Collecting, 1)]
    [InlineData(KycFileStatus.ComplementRequired, 1)]
    [InlineData(KycFileStatus.Verifying, 2)]
    [InlineData(KycFileStatus.Validating, 2)]
    [InlineData(KycFileStatus.Simplified, 2)] // validé, mais il reste le KYC complet
    [InlineData(KycFileStatus.Full, 3)]
    [InlineData(KycFileStatus.UnderReview, 3)]
    public void The_list_progress_follows_the_file_status(KycFileStatus status, int expected)
        => KycModuleFacade.CompletedSteps(status).Should().Be(expected);
}
