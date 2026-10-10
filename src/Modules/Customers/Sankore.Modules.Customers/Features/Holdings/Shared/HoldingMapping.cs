namespace Sankore.Modules.Customers.Features.Holdings.Shared;

using Sankore.Modules.Customers.Domain;
using Sankore.Shared.Kernel;

/// <summary>Translation between the holding DTOs and the domain (BNF-B-14, BNF-B-15).</summary>
internal static class HoldingMapping
{
    internal static Result<HoldingSpecifics> ToSpecifics(HoldingSpecificsDto? dto)
        => dto is null
            ? Result.Ok(HoldingSpecifics.None)
            : HoldingSpecifics.Create(
                insuredCapital:    dto.InsuredCapital,
                premium:           dto.Premium,
                periodicity:       dto.Periodicity,
                durationMonths:    dto.DurationMonths,
                annualRatePercent: dto.AnnualRatePercent,
                initialDeposit:    dto.InitialDeposit,
                currency:          dto.Currency,
                effectiveFrom:     dto.EffectiveFrom,
                notes:             dto.Notes);

    internal static HoldingSpecificsDto ToDto(HoldingSpecifics specifics) => new(
        InsuredCapital:    specifics.InsuredCapital,
        Premium:           specifics.Premium,
        Periodicity:       specifics.Periodicity,
        DurationMonths:    specifics.DurationMonths,
        AnnualRatePercent: specifics.AnnualRatePercent,
        InitialDeposit:    specifics.InitialDeposit,
        Currency:          specifics.Currency,
        EffectiveFrom:     specifics.EffectiveFrom,
        Notes:             specifics.Notes);

    internal static Result<List<DesignationRequest>> ToRequests(
        IReadOnlyList<DesignationInput>? inputs)
    {
        var requests = new List<DesignationRequest>();

        foreach (var input in inputs ?? [])
        {
            if (!Enum.TryParse<DesignationSubjectType>(
                    input.SubjectType, ignoreCase: true, out var subjectType))
                return Result.Fail<List<DesignationRequest>>(CustomerErrors.DesignationSubjectInvalid);

            if (string.IsNullOrWhiteSpace(input.RoleCode))
                return Result.Fail<List<DesignationRequest>>(CustomerErrors.DesignationRoleNotAllowed);

            requests.Add(new DesignationRequest(
                RoleCode:             input.RoleCode,
                SubjectType:          subjectType,
                SubjectId:            input.SubjectId,
                ExternalFullName:     input.ExternalFullName,
                ExternalRelationship: input.ExternalRelationship,
                Rank:                 input.Rank,
                SharePercent:         input.SharePercent));
        }

        return Result.Ok(requests);
    }

    /// <summary>
    /// Projects a holding, with the clause as it stands.
    /// </summary>
    /// <param name="subjectLabels">
    /// Resolved names, keyed by subject id. Passed in rather than looked up here so the caller
    /// makes ONE batched query for every designation of every holding on the page — a per-row
    /// lookup is the N+1 that a client's product list would hit hardest.
    /// </param>
    internal static HoldingDto ToDto(
        ProductHolding holding, IReadOnlyDictionary<Guid, string> subjectLabels) => new(
        HoldingId:       holding.Id,
        ClientId:        holding.ClientId,
        ProductLineId:   holding.ProductLineId,
        ProductCode:     holding.ProductCode,
        ProductCategory: holding.ProductCategory,
        Status:          holding.Status.ToString(),
        Origin:          holding.Origin.ToString(),
        Specifics:       ToDto(holding.Specifics),
        SignedAt:        holding.SignedAt,
        ExternalRef:     holding.ExternalRef,
        ExternalSystem:  holding.ExternalSystem,
        SyncedAt:        holding.SyncedAt,
        RejectionReason: holding.RejectionReason,
        ClauseVersion:   holding.ClauseVersion,
        CanEdit:         holding.IsEditable,
        IsReadOnly:      holding.IsReadOnly,
        AvailableActions: holding.AvailableActions,
        SourceLeadId:    holding.SourceLeadId,
        CreatedAt:       holding.CreatedAt,
        UpdatedAt:       holding.UpdatedAt,
        Designations:
        [
            .. holding.CurrentDesignations
                .OrderBy(d => d.Rank)
                .ThenBy(d => d.RoleCode)
                .Select(d => new DesignationDto(
                    DesignationId:        d.Id,
                    RoleCode:             d.RoleCode,
                    SubjectType:          d.SubjectType.ToString(),
                    SubjectId:            d.SubjectId,
                    SubjectLabel:         d.SubjectId is { } id && subjectLabels.TryGetValue(id, out var label)
                                              ? label
                                              : d.ExternalFullName,
                    ExternalRelationship: d.ExternalRelationship,
                    Rank:                 d.Rank,
                    SharePercent:         d.SharePercent,
                    Status:               d.Status.ToString(),
                    StatusReason:         d.StatusReason,
                    ClauseVersion:        d.ClauseVersion,
                    ValidFrom:            d.ValidFrom,
                    ValidTo:              d.ValidTo))
        ]);

    /// <summary>
    /// The SIGNATURE reference is deliberately absent from <see cref="HoldingDto"/>. It points at
    /// stored evidence of a client's consent, and a listing is not the place to hand out a path to
    /// it — <see cref="HoldingDto.SignedAt"/> answers the question a screen actually asks ("is it
    /// signed, and when").
    /// </summary>
    internal const string WhySignatureReferenceIsNotProjected =
        "Signature evidence is reached through its own audited route, never through a listing.";
}
