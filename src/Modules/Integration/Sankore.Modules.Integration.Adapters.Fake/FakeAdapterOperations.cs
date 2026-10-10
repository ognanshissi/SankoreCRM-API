namespace Sankore.Modules.Integration.Adapters.Fake;

/// <summary>
/// Names <see cref="FakeAdapter"/> files its recorded calls under.
///
/// <para>
/// Constants rather than literals at the call sites because a test asserting "exactly one write
/// happened" compares against these: a typo in a magic string would make the assertion pass by
/// matching nothing, which is the one way such a test can lie.
/// </para>
/// </summary>
public static class FakeAdapterOperations
{
    public const string CheckHealth = "check-health";

    public const string CreateCustomer = "create-customer";
    public const string UpdateCustomer = "update-customer";
    public const string SetKycLevel = "set-kyc-level";

    /// <summary>The optional read of the tier, mirroring <see cref="SetKycLevel"/>.</summary>
    public const string ReadKycLevel = "read-kyc-level";

    public const string OpenAccount = "open-account";
    public const string GetAccounts = "get-accounts";
    public const string GetBalance = "get-balance";
    public const string DebitAccount = "debit-account";
    public const string ReverseDebit = "reverse-debit";

    public const string GetTransactions = "get-transactions";
    public const string GetMonthlyFlow = "get-monthly-flow";

    public const string SubmitLoanApplication = "submit-loan-application";
    public const string GetLoans = "get-loans";

    public const string GetProductCodes = "get-product-codes";
    public const string Price = "price";
    public const string CheckEligibility = "check-eligibility";

    public const string Subscribe = "subscribe";
    public const string GetPolicies = "get-policies";
    public const string GetPolicy = "get-policy";
    public const string GetCertificate = "get-certificate";
    public const string CancelPolicy = "cancel-policy";

    public const string DeclareClaim = "declare-claim";
    public const string GetClaims = "get-claims";
    public const string GetClaim = "get-claim";
    public const string AddClaimDocument = "add-claim-document";
}

/// <summary>
/// One call the double answered, so a test can assert on what was asked rather than only on the
/// answer. <paramref name="IdempotencyKey"/> is null for a read, which has none.
/// </summary>
public sealed record FakeAdapterCall(string Operation, string? Target, string? IdempotencyKey);
