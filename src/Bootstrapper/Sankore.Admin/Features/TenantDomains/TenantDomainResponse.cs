namespace Sankore.Admin.Features.TenantDomains;

public record TenantDomainResponse(
    Guid Id,
    Guid TenantId,
    string Fqdn,
    bool IsPrimary,
    bool IsActive,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);