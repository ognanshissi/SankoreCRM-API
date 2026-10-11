namespace Sankore.Modules.Administration.Features.Credentials;

using Sankore.Modules.Administration.Domain;

/// <summary>
/// A professional card as a screen needs it. <b>The card number is deliberately absent</b>: it
/// identifies a named person on a regulator's register, and nothing an operator does with this
/// row requires reading it back. What they need is whether the licence holds and when it ends.
/// </summary>
/// <param name="IsCurrent">False for a superseded or withdrawn row, which the history keeps.</param>
/// <param name="IsValidToday">
/// Whether the holder may present insurance operations today — the single definition in
/// <c>DistributionCredential.IsValidAt</c>, not a second comparison made here.
/// </param>
/// <param name="HasIndemnityCover">
/// Whether professional indemnity cover is recorded at all. Exposed separately from the validity
/// because an ABSENT cover does not invalidate the card while a LAPSED one does, and a screen has
/// to be able to say which.
/// </param>
public sealed record CredentialDto(
    Guid Id,
    Guid UserId,
    string Category,
    string IssuingAuthority,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string? IndemnityInsurer,
    string? IndemnityPolicyReference,
    DateOnly? IndemnityExpiresOn,
    bool HasIndemnityCover,
    int? TrainingHours,
    DateOnly? TrainingCompletedOn,
    bool IsCurrent,
    bool IsValidToday,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    string? ClosedReason)
{
    public static CredentialDto From(DistributionCredential c, DateOnly today) => new(
        Id: c.Id,
        UserId: c.UserId,
        Category: c.Category.ToString(),
        IssuingAuthority: c.IssuingAuthority,
        IssuedOn: c.IssuedOn,
        ExpiresOn: c.ExpiresOn,
        IndemnityInsurer: c.IndemnityInsurer,
        IndemnityPolicyReference: c.IndemnityPolicyReference,
        IndemnityExpiresOn: c.IndemnityExpiresOn,
        HasIndemnityCover: c.HasIndemnityCover,
        TrainingHours: c.TrainingHours,
        TrainingCompletedOn: c.TrainingCompletedOn,
        IsCurrent: c.IsCurrent,
        IsValidToday: c.IsValidAt(today),
        ValidFrom: c.ValidFrom,
        ValidTo: c.ValidTo,
        ClosedReason: c.ClosedReason);
}
