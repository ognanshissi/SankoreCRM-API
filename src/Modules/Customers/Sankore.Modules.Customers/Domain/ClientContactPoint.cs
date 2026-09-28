namespace Sankore.Modules.Customers.Domain;

using Sankore.Shared.Kernel;

/// <summary>
/// A phone, e-mail or postal address of a client, historized rather than overwritten:
/// closing one sets <see cref="ValidTo"/> so the previous value stays auditable.
/// The clear value never reaches this entity — only its ciphertext and blind index.
/// </summary>
public sealed class ClientContactPoint
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ClientId { get; private set; }
    public ContactPointType Type { get; private set; }

    /// <summary>AES-256-GCM ciphertext produced by <c>IFieldEncryptor</c>.</summary>
    public string EncryptedValue { get; private set; } = default!;

    /// <summary>HMAC of the normalized value — the only thing queries are allowed to match on.</summary>
    public string BlindIndex { get; private set; } = default!;

    public string? Label { get; private set; }
    public bool IsPrimary { get; private set; }
    public DateTimeOffset ValidFrom { get; private set; }
    public DateTimeOffset? ValidTo { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsActive => ValidTo is null;

    private ClientContactPoint() { } // EF Core

    internal static ClientContactPoint Create(
        Guid tenantId,
        Guid clientId,
        ContactPointType type,
        string encryptedValue,
        string blindIndex,
        string? label,
        bool isPrimary,
        DateTimeOffset validFrom,
        Guid createdBy)
    {
        if (string.IsNullOrWhiteSpace(encryptedValue))
            throw new DomainException("Contact point value is required.", "ClientContactPoint.Value.Required");
        if (string.IsNullOrWhiteSpace(blindIndex))
            throw new DomainException("Contact point blind index is required.", "ClientContactPoint.BlindIndex.Required");

        return new ClientContactPoint
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ClientId = clientId,
            Type = type,
            EncryptedValue = encryptedValue,
            BlindIndex = blindIndex,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            IsPrimary = isPrimary,
            ValidFrom = validFrom,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    internal void Close(DateTimeOffset at) => ValidTo ??= at;

    internal void SetPrimary(bool value) => IsPrimary = value;

    /// <summary>Re-parents the contact point onto the surviving client of a merge.</summary>
    internal void ReassignTo(Guid clientId) => ClientId = clientId;
}
