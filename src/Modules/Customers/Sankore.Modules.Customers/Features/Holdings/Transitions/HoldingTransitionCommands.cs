namespace Sankore.Modules.Customers.Features.Holdings.Transitions;

using MediatR;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

/// <summary>
/// Declares the file complete and awaiting the client's signature (BNF-B-14).
///
/// <para>
/// It REFUSES when the product line requires a designation role the clause does not satisfy: a
/// contract sent for signature with no beneficiary where the line demands one is a contract the
/// insurer rejects after the client has signed it.
/// </para>
/// </summary>
public sealed record RequestSignatureCommand(Guid HoldingId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}

/// <summary>
/// Records the client's signature and where its evidence lives (BNF-B-14/B-16).
/// </summary>
/// <param name="SignedAt">
/// When the client actually signed, which is not necessarily now — a paper signature is captured
/// afterwards. Null means now.
/// </param>
public sealed record SignHoldingCommand(
    Guid HoldingId,
    string SignatureReference,
    DateTimeOffset? SignedAt = null
) : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}

/// <summary>
/// Queues a signed holding for its back office (BNF-B-16). The signature is the gate, and it is
/// re-checked in the aggregate rather than inferred from the status.
/// </summary>
public sealed record SubmitHoldingCommand(Guid HoldingId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}

/// <summary>
/// Sends a rejected holding back for correction (BNF-B-14). The signature does NOT survive: what
/// the client signed is no longer what would be sent.
/// </summary>
public sealed record ReturnHoldingToDraftCommand(Guid HoldingId)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}

/// <summary>Ends a holding — surrendered, matured, or cancelled before signature.</summary>
public sealed record CloseHoldingCommand(Guid HoldingId, string Reason)
    : IRequest<Result>, ICommand, IResourceCommand
{
    public string ResourceType => "ProductHolding";
    public string? ResourceId => HoldingId.ToString();
}
