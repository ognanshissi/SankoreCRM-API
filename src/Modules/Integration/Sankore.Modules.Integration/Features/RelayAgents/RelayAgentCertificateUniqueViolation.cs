namespace Sankore.Modules.Integration.Features.RelayAgents;

using Microsoft.EntityFrameworkCore;
using Npgsql;

/// <summary>
/// Recognises the violation of <c>ux_integration_relay_agent_certificate</c> — the filtered unique
/// index on <c>certificate_thumbprint</c> that makes one certificate belong to at most one agent,
/// across every tenant.
///
/// <para>
/// That index is not a convenience: it is what stops an enrolment from binding a certificate
/// another agent already uses, which would make two agents indistinguishable to
/// <see cref="IRelayAgentAdmission"/> — and the one being impersonated would be in another
/// tenant's network. It is enforced by the database rather than by a read in the handler, because
/// a read-then-write check loses the race it exists to prevent: two exchanges arriving together
/// both read "free" and both write.
/// </para>
///
/// <para>
/// So the violation has to be CAUGHT and turned into the same opaque refusal as every other
/// enrolment failure. Letting it escape would be two faults at once: a 500 for whoever is
/// installing an agent, and an oracle — an unauthenticated caller learning that a particular
/// thumbprint is already known to the platform.
/// </para>
///
/// <para>
/// Same device, and the same message fallback, as <c>KycLimitAlertUniqueViolation</c> and M01's
/// <c>GroupUniqueViolation</c>: the constraint name is matched on
/// <see cref="PostgresException"/> where Npgsql reports it, and on the exception message
/// otherwise, so the branch stays reachable under a provider that surfaces no
/// <see cref="PostgresException"/> at all.
/// </para>
/// </summary>
internal static class RelayAgentCertificateUniqueViolation
{
    /// <summary>PostgreSQL SQLSTATE for "unique_violation".</summary>
    private const string UniqueViolationSqlState = "23505";

    /// <summary>
    /// The index name as <c>IntegrationRelayAgentConfiguration</c> declares it. Referenced and
    /// never retyped: a typo here would turn the refusal into an unhandled 500 — or, worse, into
    /// a swallowed exception that also hides a genuine write failure.
    /// </summary>
    internal const string CertificateIndex = "ux_integration_relay_agent_certificate";

    internal static bool IsCertificateAlreadyBound(this DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex.InnerException is PostgresException { SqlState: UniqueViolationSqlState } pg)
        {
            return pg.ConstraintName is null
                   || pg.ConstraintName.Contains(CertificateIndex, StringComparison.OrdinalIgnoreCase);
        }

        return Mentions(ex.Message) || Mentions(ex.InnerException?.Message);
    }

    private static bool Mentions(string? message)
        => message is not null && message.Contains(CertificateIndex, StringComparison.OrdinalIgnoreCase);
}
