namespace Sankore.Modules.Leads.Features.Opportunities;

using Sankore.Modules.Leads.Domain;

/// <summary>
/// The error codes this zone answers with, in one place so the endpoints and the handlers cannot
/// drift on a spelling — a mapped code that nobody raises reads as a 422 nobody can explain.
/// </summary>
internal static class OpportunityErrors
{
    internal const string NotFound = "OPPORTUNITY_NOT_FOUND";

    /// <summary>Raised by the aggregate itself. Repeated here so the endpoint can map it.</summary>
    internal const string AlreadyClosed = "OPPORTUNITY_ALREADY_CLOSED";

    internal const string InvalidCloseOutcome = "INVALID_CLOSE_OUTCOME";

    /// <summary>
    /// <c>PUT /stage</c> was handed a closed stage. Its own code rather than reusing
    /// <see cref="InvalidCloseOutcome"/>: the caller used the wrong endpoint, and the message has
    /// to say which one is right.
    /// </summary>
    internal const string CloseThroughCloseEndpoint = "OPPORTUNITY_CLOSE_THROUGH_CLOSE_ENDPOINT";
}

/// <summary>
/// The staleness check <c>Lead</c> already has (<c>LeadConcurrency</c>), for the one aggregate of
/// this zone that can be edited from two screens at once.
/// </summary>
/// <remarks>
/// Compared at MILLISECOND granularity, like its lead twin: a <c>DateTimeOffset</c> round-tripped
/// through JSON loses sub-millisecond precision, so an exact comparison would report a conflict on
/// every second save.
/// </remarks>
internal static class OpportunityConcurrency
{
    internal static bool IsStale(this Opportunity opportunity, DateTimeOffset? expectedUpdatedAt)
    {
        if (expectedUpdatedAt is not { } expected) return false;

        return Math.Abs((opportunity.UpdatedAt - expected).TotalMilliseconds) > 1;
    }
}
