namespace Sankore.Modules.Integration.Tests.Adapters;

using Sankore.Modules.Integration.Adapters.Fake;
using Sankore.Modules.Integration.Ports;
using Sankore.Modules.Integration.PublicApi;
using Sankore.Modules.Integration.Tests.Contract;

/// <summary>
/// The in-memory double, run against the shared port contracts (INT-10).
///
/// <para>
/// Kept in one file, as the object-storage suite does: each class is three lines of wiring, and
/// splitting them across seven files would hide the one thing worth seeing at a glance — that the
/// SAME adapter satisfies all seven contracts, which is acceptance criterion 1.
/// </para>
///
/// <para>
/// A fresh <see cref="FakeAdapter"/> per <c>CreatePort</c> call: the double keeps what a flow
/// wrote, so sharing one instance between facts would let the account one test opened change what
/// the next test lists.
/// </para>
/// </summary>
public sealed class FakeAdapterCbsCustomerPortTests : CbsCustomerPortContractTests
{
    protected override ICbsCustomerPort CreatePort() => new FakeAdapter();

    protected override ICbsCustomerPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override ICbsCustomerPort CreateDuplicatingPort() => FakeAdapter.Duplicating();

    protected override int? WriteCount(ICbsCustomerPort port) => FakeAdapterWrites.CountOn(port);
}

public sealed class FakeAdapterCbsAccountPortTests : CbsAccountPortContractTests
{
    protected override ICbsAccountPort CreatePort() => new FakeAdapter();

    protected override ICbsAccountPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override ICbsAccountPort CreateDuplicatingPort() => FakeAdapter.Duplicating();

    protected override ExternalId ExistingCustomerId => new(FakeAdapter.SeededCustomerId);

    protected override ExternalId ExistingAccountId => new(FakeAdapter.SeededAccountId);

    protected override string ExistingProductCode => FakeAdapter.SeededProductCode;

    protected override int? WriteCount(ICbsAccountPort port) => FakeAdapterWrites.CountOn(port);
}

public sealed class FakeAdapterCbsTransactionPortTests : CbsTransactionPortContractTests
{
    protected override ICbsTransactionPort CreatePort() => new FakeAdapter();

    protected override ICbsTransactionPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override ExternalId ExistingCustomerId => new(FakeAdapter.SeededCustomerId);

    protected override ExternalId ExistingAccountId => new(FakeAdapter.SeededAccountId);
}

public sealed class FakeAdapterCbsLoanPortTests : CbsLoanPortContractTests
{
    protected override ICbsLoanPort CreatePort() => new FakeAdapter();

    protected override ICbsLoanPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override ICbsLoanPort CreateDuplicatingPort() => FakeAdapter.Duplicating();

    protected override ExternalId ExistingCustomerId => new(FakeAdapter.SeededCustomerId);

    protected override string ExistingProductCode => FakeAdapter.SeededProductCode;

    protected override int? WriteCount(ICbsLoanPort port) => FakeAdapterWrites.CountOn(port);
}

public sealed class FakeAdapterInsuranceProductPortTests : InsuranceProductPortContractTests
{
    protected override IInsuranceProductPort CreatePort() => new FakeAdapter();

    protected override IInsuranceProductPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override string ExistingProductCode => FakeAdapter.SeededInsurerProductCode;
}

public sealed class FakeAdapterInsurancePolicyPortTests : InsurancePolicyPortContractTests
{
    protected override IInsurancePolicyPort CreatePort() => new FakeAdapter();

    protected override IInsurancePolicyPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override IInsurancePolicyPort CreateDuplicatingPort() => FakeAdapter.Duplicating();

    protected override ExternalId ExistingCustomerId => new(FakeAdapter.SeededCustomerId);

    protected override ExternalId ExistingPolicyId => new(FakeAdapter.SeededPolicyId);

    protected override string ExistingProductCode => FakeAdapter.SeededInsurerProductCode;

    protected override int? WriteCount(IInsurancePolicyPort port) => FakeAdapterWrites.CountOn(port);
}

public sealed class FakeAdapterInsuranceClaimPortTests : InsuranceClaimPortContractTests
{
    protected override IInsuranceClaimPort CreatePort() => new FakeAdapter();

    protected override IInsuranceClaimPort CreateTimingOutPort() => FakeAdapter.TimingOut();

    protected override IInsuranceClaimPort CreateDuplicatingPort() => FakeAdapter.Duplicating();

    protected override ExternalId ExistingPolicyId => new(FakeAdapter.SeededPolicyId);

    protected override ExternalId ExistingClaimId => new(FakeAdapter.SeededClaimId);

    protected override int? WriteCount(IInsuranceClaimPort port) => FakeAdapterWrites.CountOn(port);
}

/// <summary>
/// Reads the double's own call journal, which is the half of the idempotency contract an HTTP
/// adapter cannot report. A call carrying an idempotency key IS a write, so counting them needs no
/// list of operation names to keep in step.
/// </summary>
internal static class FakeAdapterWrites
{
    public static int? CountOn(object port) => port is FakeAdapter fake
        ? fake.Calls.Count(c => c.IdempotencyKey is not null)
        : null;
}
