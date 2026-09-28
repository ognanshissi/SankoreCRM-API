namespace Sankore.Shared.Kernel.Authorization;

/// <summary>
/// Marker for a MediatR request that targets ONE agency, so that
/// <c>AgencyAuthorizationBehavior</c> can reject it with
/// <c>AGENCY_OUT_OF_SCOPE</c> before the handler runs.
///
/// Declared in the Kernel (not in Shared.Infrastructure) so that a module's
/// commands can implement it without pulling MediatR pipeline types in.
/// </summary>
public interface IAgencyScopedRequest
{
    /// <summary>
    /// Agency the request acts upon. <c>null</c> means "not agency-scoped for
    /// this invocation" and the authorization check is skipped.
    /// </summary>
    Guid? TargetAgencyId { get; }
}
