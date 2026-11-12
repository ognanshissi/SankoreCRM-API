namespace Sankore.Modules.Leads.Features.Opportunities.Quote;

using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Opportunities.History;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.Visibility;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

// Quote lines, lot 3 (2026-11-10, product owner; docs/devis-en-lignes.md): the lines of a project's
// offer, their totals, and the manager's decision on a discount beyond the institution's ceiling.

public sealed record ProjectQuoteLineDto(
    Guid Id, Guid ProductId, string ProductCode, string Label, decimal Quantity, string? Unit,
    decimal UnitPrice, decimal? CatalogueUnitPrice, QuoteDiscountKind DiscountKind, decimal DiscountValue,
    ProductVatCategory VatCategory, decimal VatRate,
    decimal GrossExclTax, decimal DiscountAmount, decimal NetExclTax, decimal VatAmount, decimal TotalInclTax);

/// <param name="Quotable">Whether this project takes lines: no for a credit, savings or insurance project.</param>
/// <param name="NotQuotableReason">Why not, in French, for the screen.</param>
/// <param name="DiscountCeilingPercent">The institution's ceiling today; null = none.</param>
/// <param name="CanDecide">Whether the caller may approve or refuse a pending discount.</param>
/// <param name="CanBeIssued">Whether a quote may be generated from these lines (lot 5).</param>
public sealed record ProjectQuoteDto(
    Guid OpportunityId, bool Quotable, string? NotQuotableReason, string Currency,
    IReadOnlyList<ProjectQuoteLineDto> Lines,
    QuoteDiscountKind GlobalDiscountKind, decimal GlobalDiscountValue,
    decimal SubtotalExclTax, decimal GlobalDiscountAmount, decimal TotalExclTax, decimal TotalVat, decimal TotalInclTax,
    IReadOnlyList<QuoteVatBreakdown> VatByRate,
    decimal EffectiveDiscountPercent, decimal? DiscountCeilingPercent,
    QuoteApprovalStatus ApprovalStatus, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionComment,
    bool CanDecide, bool CanBeIssued, DateTimeOffset? UpdatedAt,
    string? Number = null, bool ChangedSinceIssue = false, IReadOnlyList<ProjectQuoteIssueDto>? Issues = null);

/// <summary>One quote PDF generated from the offer (lot 5), newest first.</summary>
/// <param name="AttachmentId">The project file: <c>leads/opportunities/{id}/attachments/{attachmentId}/content</c>, and what the sending picks.</param>
public sealed record ProjectQuoteIssueDto(
    Guid Id, string Label, string Number, int Version, DateTimeOffset IssuedAt, string? IssuedByName, DateOnly ValidUntil,
    decimal TotalExclTax, decimal TotalInclTax, string Currency, Guid AttachmentId, string? FileName,
    DateTimeOffset? LastSentAt, string? LastSentChannel);

/// <param name="Id">Sent back for a line already saved: it keeps its VAT rate and catalogue price as long as its product stays the same.</param>
/// <param name="Label">Null = the product's name.</param>
/// <param name="Unit">Null = the product's unit.</param>
/// <param name="UnitPrice">Excluding VAT; null = the catalogue's price (refused when it has none).</param>
public sealed record ProjectQuoteLineInput(
    Guid? Id, Guid ProductId, decimal Quantity, string? Label = null, string? Unit = null, decimal? UnitPrice = null,
    QuoteDiscountKind DiscountKind = QuoteDiscountKind.None, decimal DiscountValue = 0);

/// <summary>
/// Every read of another module from these commands runs outside their ambient transaction — the
/// rule of this solution: a second connection enlisting in it makes the next suppressed read fail
/// (« This connection was used with an ambient transaction »), found in this lot's recette.
/// </summary>
internal static class Outside
{
    public static async Task<T> RunAsync<T>(Func<Task<T>> read)
    {
        using var scope = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress, System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        var value = await read();
        scope.Complete();
        return value;
    }
}

internal static class ProjectQuoteErrors
{
    public const string FinanceProject = "QUOTE_FINANCE_PROJECT";
    public const string ProductNotFound = "QUOTE_PRODUCT_NOT_FOUND";
    public const string ProductNotStandard = "QUOTE_PRODUCT_NOT_STANDARD";
    public const string ProductInactive = "QUOTE_PRODUCT_INACTIVE";
    public const string PriceRequired = "QUOTE_LINE_PRICE_REQUIRED";
    public const string NotPending = "QUOTE_NOT_PENDING_APPROVAL";
    public const string ApproverNotAllowed = "QUOTE_APPROVER_NOT_ALLOWED";
    public const string SelfApproval = "QUOTE_SELF_APPROVAL_FORBIDDEN";
    public const string Unavailable = "QUOTE_CATALOGUE_UNAVAILABLE";
}

/// <summary>What the quote slices share: the project in the caller's perimeter, the « quotable » rule, the DTO.</summary>
internal sealed class ProjectQuotes(
    LeadsDbContext db,
    IQuoteIssuer issuer,
    IAdministrationModule administration,
    ICurrentUser me,
    ProjectTemplateResolver templates,
    LeadVisibility? visibility = null)
{
    public IQueryable<Opportunity> Visible()
        => visibility is not null ? visibility.Apply(db.Opportunities, db.Leads) : db.Opportunities;

    /// <summary>
    /// Lines are for the socle's Standard products only (product owner): a project whose template
    /// carries a credit, a deposit or a premium, or whose product is a module's, keeps its single
    /// product and its typed amount. Null = quotable.
    /// </summary>
    public async Task<string?> NotQuotableReasonAsync(Opportunity project, ProjectQuote? quote, CancellationToken ct)
    {
        var template = await templates.ForProjectAsync(project, ct);
        if (template.AmountKind != ProjectAmountKind.Generic)
            return "Un projet de crédit, d’épargne ou d’assurance garde un seul produit.";
        if (quote is { Lines.Count: > 0 } || project.ProductLineId is not { } lineId) return null;
        var products = await issuer.GetProductsAsync(project.TenantId, [lineId], ct);
        return products is [{ IsStandard: false }, ..]
            ? "Le produit de ce projet relève d’un module (crédit, épargne, assurance) : pas de lignes."
            : null;
    }

    /// <summary>
    /// Who may decide a discount beyond the ceiling: a manager (lead:team:supervise) whose team
    /// holds the project's owner — or who covers every agency — and who is neither that owner nor
    /// the person who saved the lines. Four eyes, like the KYC rungs.
    /// </summary>
    public async Task<string?> DecisionRefusalAsync(Opportunity project, ProjectQuote quote, CancellationToken ct)
    {
        if (!TeamSupervision.Supervises(me)) return ProjectQuoteErrors.ApproverNotAllowed;
        if (me.Id == project.OwnerId || me.Id == quote.UpdatedBy) return ProjectQuoteErrors.SelfApproval;
        if (TeamSupervision.CoversAllAgencies(me) || project.OwnerId is not { } owner) return null;
        var team = await Outside.RunAsync(() => administration.GetTeamAgentIdsAsync(project.TenantId, me.Id, ct));
        return team.Contains(owner) ? null : ProjectQuoteErrors.ApproverNotAllowed;
    }

    public async Task<ProjectQuoteDto> ToDtoAsync(Opportunity project, ProjectQuote? quote, CancellationToken ct)
    {
        var rules = await issuer.GetAsync(project.TenantId, ct);
        var reason = await NotQuotableReasonAsync(project, quote, ct);
        if (quote is null)
            return new ProjectQuoteDto(project.Id, reason is null, reason, rules.Currency, [],
                QuoteDiscountKind.None, 0, 0, 0, 0, 0, 0, [], 0, rules.MaxDiscountPercent,
                QuoteApprovalStatus.NotRequired, null, null, null, false, false, null);

        var totals = QuoteTotals.Compute(quote.Lines, quote.GlobalDiscountKind, quote.GlobalDiscountValue, quote.Currency);
        var byRate = totals.IsSuccess ? totals.Value.VatByRate : [];
        string? decidedBy = null;
        if (quote.DecidedBy is { } who)
            decidedBy = (await Outside.RunAsync(() => administration.GetUserNamesAsync(project.TenantId, [who], ct))).GetValueOrDefault(who);
        var canDecide = quote.ApprovalStatus == QuoteApprovalStatus.Pending && await DecisionRefusalAsync(project, quote, ct) is null;
        var issues = await IssuesAsync(project, ct);

        return new ProjectQuoteDto(
            project.Id, reason is null, reason, quote.Currency,
            quote.Lines.OrderBy(l => l.Position).Select(l => new ProjectQuoteLineDto(
                l.Id, l.ProductId, l.ProductCode, l.Label, l.Quantity, l.Unit, l.UnitPrice, l.CatalogueUnitPrice,
                l.DiscountKind, l.DiscountValue, l.VatCategory, l.VatRate,
                l.GrossExclTax, l.DiscountAmount, l.NetExclTax, l.VatAmount, l.TotalInclTax)).ToList(),
            quote.GlobalDiscountKind, quote.GlobalDiscountValue,
            quote.SubtotalExclTax, quote.GlobalDiscountAmount, quote.TotalExclTax, quote.TotalVat, quote.TotalInclTax,
            byRate, quote.EffectiveDiscountPercent, quote.DiscountCeilingPercent,
            quote.ApprovalStatus, decidedBy, quote.DecidedAt, quote.DecisionComment,
            canDecide, quote.CanBeIssued, quote.UpdatedAt,
            quote.Number, quote.ChangedSinceIssue, issues);
    }

    private async Task<IReadOnlyList<ProjectQuoteIssueDto>> IssuesAsync(Opportunity project, CancellationToken ct)
    {
        var issues = await db.ProjectQuoteIssues.AsNoTracking()
            .Where(i => i.OpportunityId == project.Id)
            .OrderByDescending(i => i.Version)
            .ToListAsync(ct);
        if (issues.Count == 0) return [];
        var fileIds = issues.Select(i => i.AttachmentId).ToList();
        var files = await db.ProjectAttachments.AsNoTracking()
            .Where(a => fileIds.Contains(a.Id) && a.DeletedAt == null)
            .ToDictionaryAsync(a => a.Id, ct);
        var authors = issues.Where(i => i.IssuedBy is not null).Select(i => i.IssuedBy!.Value).Distinct().ToList();
        var names = authors.Count == 0 ? new Dictionary<Guid, string>()
            : await Outside.RunAsync(() => administration.GetUserNamesAsync(project.TenantId, authors, ct));
        return issues.Select(i =>
        {
            files.TryGetValue(i.AttachmentId, out var file);
            return new ProjectQuoteIssueDto(
                i.Id, ProjectQuote.Label(i.Number, i.Version), i.Number, i.Version, i.IssuedAt,
                i.IssuedBy is { } by ? names.GetValueOrDefault(by) : null, i.ValidUntil,
                i.TotalExclTax, i.TotalInclTax, i.Currency, i.AttachmentId, file?.FileName,
                file?.LastSentAt, file?.LastSentChannel);
        }).ToList();
    }
}

// ── GET ─────────────────────────────────────────────────────────────────────────

internal sealed record GetProjectQuoteQuery(Guid OpportunityId) : IRequest<Result<ProjectQuoteDto>>;

internal sealed class GetProjectQuoteHandler(LeadsDbContext db, ProjectQuotes quotes)
    : IRequestHandler<GetProjectQuoteQuery, Result<ProjectQuoteDto>>
{
    public async Task<Result<ProjectQuoteDto>> Handle(GetProjectQuoteQuery q, CancellationToken ct)
    {
        var project = await quotes.Visible().AsNoTracking().FirstOrDefaultAsync(o => o.Id == q.OpportunityId, ct);
        if (project is null) return Result.Fail<ProjectQuoteDto>(OpportunityErrors.NotFound);
        var quote = await db.ProjectQuotes.AsNoTracking().FirstOrDefaultAsync(x => x.OpportunityId == project.Id, ct);
        return Result.Ok(await quotes.ToDtoAsync(project, quote, ct));
    }
}

// ── PUT lines ───────────────────────────────────────────────────────────────────

internal sealed record SetProjectQuoteCommand(
    Guid OpportunityId, IReadOnlyList<ProjectQuoteLineInput> Lines,
    QuoteDiscountKind GlobalDiscountKind, decimal GlobalDiscountValue, DateTimeOffset? ExpectedUpdatedAt)
    : IRequest<Result<ProjectQuoteDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class SetProjectQuoteHandler(
    LeadsDbContext db,
    ProjectQuotes quotes,
    IQuoteIssuer issuer,
    IAdministrationModule administration,
    ProjectTemplateResolver templates,
    ProjectHistoryRecorder history,
    ICurrentUser me,
    TimeProvider clock,
    ILogger<SetProjectQuoteHandler> logger,
    INotificationsModule? notifications = null)
    : IRequestHandler<SetProjectQuoteCommand, Result<ProjectQuoteDto>>
{
    public async Task<Result<ProjectQuoteDto>> Handle(SetProjectQuoteCommand cmd, CancellationToken ct)
    {
        var project = await quotes.Visible().AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null) return Result.Fail<ProjectQuoteDto>(OpportunityErrors.NotFound);
        if (project.Stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost)
            return Result.Fail<ProjectQuoteDto>(OpportunityErrors.AlreadyClosed);

        var quote = await db.ProjectQuotes.AsTracking().FirstOrDefaultAsync(x => x.OpportunityId == project.Id, ct);
        if (quote is not null && cmd.ExpectedUpdatedAt is { } expected
            && expected.ToUnixTimeMilliseconds() != quote.UpdatedAt.ToUnixTimeMilliseconds())
            return Result.Fail<ProjectQuoteDto>(LeadConcurrency.ConflictError);

        if (await quotes.NotQuotableReasonAsync(project, quote, ct) is not null)
            return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.FinanceProject);

        QuoteIssuer rules;
        IReadOnlyList<QuotableProduct> products;
        try
        {
            rules = await issuer.GetAsync(project.TenantId, ct);
            products = await issuer.GetProductsAsync(project.TenantId, cmd.Lines.Select(l => l.ProductId).Distinct().ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Catalogue unreadable while pricing project {ProjectId}", project.Id);
            return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.Unavailable);
        }

        var now = clock.GetUtcNow();
        var isNew = quote is null;
        quote ??= ProjectQuote.Start(project.TenantId, project.Id, rules.Currency, now);
        var previous = quote.Lines.ToDictionary(l => l.Id);

        List<ProjectQuoteLine> lines = [];
        foreach (var input in cmd.Lines)
        {
            var product = products.FirstOrDefault(p => p.Id == input.ProductId);
            if (product is null) return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.ProductNotFound);
            if (!product.IsStandard) return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.ProductNotStandard);

            // A saved line on the same product keeps what it was entered with — its rate and the
            // catalogue price its discount is measured against — even if the product left the catalogue since.
            var kept = input.Id is { } id && previous.TryGetValue(id, out var old) && old.ProductId == product.Id ? old : null;
            if (kept is null && !product.IsActive) return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.ProductInactive);

            var price = input.UnitPrice ?? product.UnitPrice;
            if (price is null) return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.PriceRequired);

            var vatCategory = kept?.VatCategory ?? product.VatCategory;
            var vatRate = kept?.VatRate ?? vatCategory switch
            {
                ProductVatCategory.Reduced => rules.VatReducedRate,
                ProductVatCategory.Exempt => 0m,
                _ => rules.VatStandardRate,
            };

            var line = ProjectQuoteLine.Create(
                kept?.Id, product.Id, product.Code, input.Label ?? kept?.Label ?? product.Name, input.Quantity,
                input.Unit ?? product.PriceUnit, price.Value, kept is null ? product.UnitPrice : kept.CatalogueUnitPrice,
                input.DiscountKind, input.DiscountValue, vatCategory, vatRate);
            if (line.IsFailure) return Result.Fail<ProjectQuoteDto>(line.Error!);
            lines.Add(line.Value);
        }

        var replaced = quote.Replace(lines, cmd.GlobalDiscountKind, cmd.GlobalDiscountValue,
            rules.MaxDiscountPercent, me.IsAuthenticated ? me.Id : null, now);
        if (replaced.IsFailure) return Result.Fail<ProjectQuoteDto>(replaced.Error!);
        if (isNew) db.ProjectQuotes.Add(quote);

        var before = ProjectHistorySnapshot.Of(project);
        if (lines.Count > 0)
        {
            var first = products.First(p => p.Id == lines[0].ProductId);
            var previousProduct = project.Product;
            var priced = project.PriceFromQuote(new Money(quote.TotalExclTax, quote.Currency), first.Name, first.Id, now);
            if (priced.IsFailure) return Result.Fail<ProjectQuoteDto>(priced.Error!);
            // Another product is another template, as on a product change by hand.
            if (project.Product != previousProduct)
                project.FollowTemplateVersion((await templates.ResolveAsync(project.Product, ct)).AttachableVersionId, now);
        }
        history.Changes(before, project);
        history.QuoteLinesChanged(project, quote);

        await db.SaveChangesAsync(ct);

        if (quote.ApprovalStatus == QuoteApprovalStatus.Pending)
            await AskManagerAsync(project, quote, ct);

        return Result.Ok(await quotes.ToDtoAsync(project, quote, ct));
    }

    /// <summary>
    /// The bell for the owner's manager (line manager, else agency head). Best effort: the offer is
    /// saved and shows « À valider » whatever happens here.
    /// </summary>
    private async Task AskManagerAsync(Opportunity project, ProjectQuote quote, CancellationToken ct)
    {
        if (notifications is null) return;
        try
        {
            var agent = project.OwnerId ?? quote.UpdatedBy;
            if (agent is null) return;
            var supervisors = await Outside.RunAsync(() => administration.GetUserSupervisorsAsync(project.TenantId, agent.Value, ct));
            if ((supervisors.LineManagerId ?? supervisors.AgencyManagerId) is not { } manager || manager == agent) return;
            var percent = quote.EffectiveDiscountPercent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));
            var title = $"Remise à valider : {project.Title}";
            await Outside.RunAsync(() => notifications.NotifyInAppAsync(new InAppNotificationRequest(
                project.TenantId, manager, "project.quote.approval",
                title.Length > 120 ? title[..120] : title,
                $"Remise de {percent} %, au-delà du plafond de l’institution.",
                $"/leads/projets/{project.Id}",
                $"project-quote-approval-{quote.Id}-{quote.UpdatedAt.ToUnixTimeMilliseconds()}"), ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Discount approval request not delivered for project {ProjectId}", project.Id);
        }
    }
}

// ── POST decision ───────────────────────────────────────────────────────────────

internal sealed record DecideProjectQuoteCommand(Guid OpportunityId, bool Approve, string? Comment)
    : IRequest<Result<ProjectQuoteDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class DecideProjectQuoteHandler(
    LeadsDbContext db,
    ProjectQuotes quotes,
    ProjectHistoryRecorder history,
    ICurrentUser me,
    TimeProvider clock,
    ILogger<DecideProjectQuoteHandler> logger,
    INotificationsModule? notifications = null)
    : IRequestHandler<DecideProjectQuoteCommand, Result<ProjectQuoteDto>>
{
    public async Task<Result<ProjectQuoteDto>> Handle(DecideProjectQuoteCommand cmd, CancellationToken ct)
    {
        var project = await quotes.Visible().AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null) return Result.Fail<ProjectQuoteDto>(OpportunityErrors.NotFound);
        var quote = await db.ProjectQuotes.AsTracking().FirstOrDefaultAsync(x => x.OpportunityId == project.Id, ct);
        if (quote is null || quote.ApprovalStatus != QuoteApprovalStatus.Pending)
            return Result.Fail<ProjectQuoteDto>(ProjectQuoteErrors.NotPending);

        if (await quotes.DecisionRefusalAsync(project, quote, ct) is { } refusal)
            return Result.Fail<ProjectQuoteDto>(refusal);

        var decided = quote.Decide(cmd.Approve, me.Id, cmd.Comment, clock.GetUtcNow());
        if (decided.IsFailure) return Result.Fail<ProjectQuoteDto>(decided.Error!);
        history.QuoteDiscountDecided(project, cmd.Approve, quote.DecisionComment);
        await db.SaveChangesAsync(ct);

        if (notifications is not null && (quote.UpdatedBy ?? project.OwnerId) is { } agent && agent != me.Id)
        {
            try
            {
                await Outside.RunAsync(() => notifications.NotifyInAppAsync(new InAppNotificationRequest(
                    project.TenantId, agent, "project.quote.approval",
                    cmd.Approve ? "Remise acceptée" : "Remise refusée",
                    cmd.Approve ? project.Title : $"{project.Title} — {quote.DecisionComment}",
                    $"/leads/projets/{project.Id}",
                    $"project-quote-decision-{quote.Id}-{quote.DecidedAt!.Value.ToUnixTimeMilliseconds()}"), ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Discount decision not delivered for project {ProjectId}", project.Id);
            }
        }
        return Result.Ok(await quotes.ToDtoAsync(project, quote, ct));
    }
}

// ── POST issue (lot 5) ──────────────────────────────────────────────────────────

/// <param name="ValidityDays">Null = the institution's default (company-info/quote-settings).</param>
/// <param name="PaymentTerms">Null = the institution's default; blank = none printed.</param>
internal sealed record IssueProjectQuoteCommand(Guid OpportunityId, int? ValidityDays, string? PaymentTerms)
    : IRequest<Result<ProjectQuoteDto>>, ICommand, IResourceCommand
{
    public string ResourceType => "Opportunity";
    public string? ResourceId => OpportunityId.ToString();
}

internal sealed class IssueProjectQuoteValidator : AbstractValidator<IssueProjectQuoteCommand>
{
    public IssueProjectQuoteValidator()
    {
        RuleFor(c => c.ValidityDays!.Value).InclusiveBetween(1, 365).When(c => c.ValidityDays.HasValue)
            .WithName(nameof(IssueProjectQuoteCommand.ValidityDays)).WithMessage("Entre 1 et 365 jours.");
        RuleFor(c => c.PaymentTerms).MaximumLength(500).WithMessage("500 caractères au plus.");
    }
}

/// <summary>
/// Generates the quote PDF of the offer as it stands and files it on the project, under the
/// reserved key <see cref="FieldKey"/> — so the existing e-mail and WhatsApp sending, the
/// « Document envoyé » trail and the client's « Documents » tab take it with nothing more.
/// </summary>
internal sealed class IssueProjectQuoteHandler(
    LeadsDbContext db,
    ProjectQuotes quotes,
    IQuoteIssuer issuer,
    ProjectHistoryRecorder history,
    ICurrentUser me,
    TimeProvider clock,
    IServiceProvider services,
    ILogger<IssueProjectQuoteHandler> logger,
    Customers.PublicApi.ICustomersModule? customers = null,
    ITenantStore? tenants = null)
    : IRequestHandler<IssueProjectQuoteCommand, Result<ProjectQuoteDto>>
{
    /// <summary>The project file key of a generated quote. Not a template field: no upload reaches it.</summary>
    public const string FieldKey = "quote";

    public async Task<Result<ProjectQuoteDto>> Handle(IssueProjectQuoteCommand cmd, CancellationToken ct)
    {
        var project = await quotes.Visible().AsTracking().FirstOrDefaultAsync(o => o.Id == cmd.OpportunityId, ct);
        if (project is null) return Result.Fail<ProjectQuoteDto>(OpportunityErrors.NotFound);
        if (project.Stage == OpportunityStage.ClosedLost) return Result.Fail<ProjectQuoteDto>(OpportunityErrors.AlreadyClosed);

        var quote = await db.ProjectQuotes.AsTracking().FirstOrDefaultAsync(q => q.OpportunityId == project.Id, ct);
        if (quote is null) return Result.Fail<ProjectQuoteDto>("QUOTE_EMPTY");
        if (!quote.CanBeIssued)
            return Result.Fail<ProjectQuoteDto>(quote.Lines.Count == 0 ? "QUOTE_EMPTY" : "QUOTE_DISCOUNT_NOT_APPROVED");

        if (services.GetService(typeof(Infrastructure.TaskAttachments.ITaskAttachmentStore)) is not Infrastructure.TaskAttachments.ITaskAttachmentStore store)
            return Result.Fail<ProjectQuoteDto>(ProjectFields.ProjectAttachmentHandlers.Unavailable);

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var rules = await issuer.GetAsync(project.TenantId, ct);
        var logo = await issuer.GetLogoAsync(project.TenantId, ct);
        var number = quote.Number ?? await issuer.NextNumberAsync(project.TenantId, today, ct);

        var version = quote.Issue(number, now);
        if (version.IsFailure) return Result.Fail<ProjectQuoteDto>(version.Error!);

        var totals = QuoteTotals.Compute(quote.Lines, quote.GlobalDiscountKind, quote.GlobalDiscountValue, quote.Currency);
        if (totals.IsFailure) return Result.Fail<ProjectQuoteDto>(totals.Error!);

        var validUntil = today.AddDays(cmd.ValidityDays ?? rules.QuoteValidityDays);
        var terms = cmd.PaymentTerms is null ? rules.QuotePaymentTerms
            : string.IsNullOrWhiteSpace(cmd.PaymentTerms) ? null : cmd.PaymentTerms.Trim();
        var label = ProjectQuote.Label(number, version.Value);
        // The « Mode test » sandbox only (a twin): a demo institution is « test » too, and its
        // quotes are shown to prospects in demonstrations.
        var testMode = tenants is not null && (await tenants.GetAsync(project.TenantId, ct))?.ParentTenantId is not null;

        var pdf = ProjectQuotePdf.Generate(new QuotePdfModel(
            rules, logo, await RecipientAsync(project, ct), project.Title, label, today, validUntil, terms,
            quote, totals.Value, testMode));

        var stored = await store.StoreAsync(project.TenantId, pdf, ct);
        var file = ProjectAttachment.Record(project.TenantId, project.Id, FieldKey, $"Devis {label}.pdf",
            "application/pdf", stored.SizeBytes, stored.Sha256, stored.StorageRef, me.Id, now);
        db.ProjectAttachments.Add(file);
        db.ProjectQuoteIssues.Add(ProjectQuoteIssue.Record(quote, version.Value, validUntil, me.IsAuthenticated ? me.Id : null, file.Id, now));
        history.QuoteIssued(project, label, file.Id);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Bytes nobody can reach are removed rather than left in the store. The number stays
            // taken: a gap, never a duplicate (IQuoteIssuer.NextNumberAsync).
            await store.DeleteAsync(project.TenantId, stored.StorageRef, CancellationToken.None);
            throw;
        }

        logger.LogInformation("Quote {Label} issued for project {ProjectId}", label, project.Id);
        return Result.Ok(await quotes.ToDtoAsync(project, quote, ct));
    }

    /// <summary>The client when the project has one, else the lead — the company first for a business.</summary>
    private async Task<QuoteRecipient> RecipientAsync(Opportunity project, CancellationToken ct)
    {
        if (project.CustomerEntityId is { } clientId && customers is not null)
        {
            var client = await Outside.RunAsync(() => customers.GetClientSummaryAsync(project.TenantId, clientId, ct));
            if (client is not null)
                return new QuoteRecipient(client.DisplayName, null, null, $"Client n° {client.ClientNumber}");
        }

        if (project.LeadId is { } leadId
            && await db.Leads.AsNoTracking().Where(l => l.Id == leadId)
                .Select(l => new { l.FullName, l.CompanyName, l.PrincipalCity })
                .FirstOrDefaultAsync(ct) is { } lead)
        {
            var company = string.IsNullOrWhiteSpace(lead.CompanyName) ? null : lead.CompanyName.Trim();
            return new QuoteRecipient(company ?? lead.FullName, company is null ? null : lead.FullName,
                string.IsNullOrWhiteSpace(lead.PrincipalCity) ? null : lead.PrincipalCity.Trim(), null);
        }

        return new QuoteRecipient(project.Title, null, null, null);
    }
}

// ── Endpoints ───────────────────────────────────────────────────────────────────

public sealed record SetProjectQuoteRequest(
    IReadOnlyList<ProjectQuoteLineInput> Lines,
    QuoteDiscountKind GlobalDiscountKind = QuoteDiscountKind.None,
    decimal GlobalDiscountValue = 0,
    DateTimeOffset? ExpectedUpdatedAt = null);

public sealed record DecideProjectQuoteRequest(bool Approve, string? Comment = null);

public sealed record IssueProjectQuoteRequest(int? ValidityDays = null, string? PaymentTerms = null);

internal static class ProjectQuoteEndpoints
{
    public static RouteGroupBuilder MapProjectQuote(this RouteGroupBuilder group)
    {
        group.MapGet("{id:guid}/quote", async (Guid id, ISender sender, CancellationToken ct) =>
                ToResult(await sender.Send(new GetProjectQuoteQuery(id), ct)))
            .WithName("GetProjectQuote")
            .WithTags("Opportunities")
            .WithSummary("The lines, discounts and totals of a project's offer")
            .WithDescription("Standard products only. An empty offer answers lines = [] and Quotable, which is false for a credit, savings or insurance project.")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Produces<ProjectQuoteDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPut("{id:guid}/quote", async (Guid id, SetProjectQuoteRequest r, ISender sender, CancellationToken ct) =>
                ToResult(await sender.Send(new SetProjectQuoteCommand(id, r.Lines ?? [], r.GlobalDiscountKind, r.GlobalDiscountValue, r.ExpectedUpdatedAt), ct)))
            .WithName("SetProjectQuote")
            .WithTags("Opportunities")
            .WithSummary("Replace the lines and the global discount of a project's offer")
            .WithDescription(
                "Prices excluding VAT; the discount comes before the VAT. The project's estimated amount becomes the "
                + "total excluding VAT and its product the first line's. A discount against the catalogue beyond the "
                + "institution's ceiling puts the offer « À valider » until a manager decides. Errors: QUOTE_FINANCE_PROJECT, "
                + "QUOTE_PRODUCT_NOT_FOUND, QUOTE_PRODUCT_NOT_STANDARD, QUOTE_PRODUCT_INACTIVE, QUOTE_LINE_PRICE_REQUIRED, "
                + "QUOTE_DISCOUNT_ABOVE_AMOUNT, QUOTE_DISCOUNT_INVALID, QUOTE_LINE_*.")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Accepts<SetProjectQuoteRequest>("application/json")
            .Produces<ProjectQuoteDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{id:guid}/quote/decision", async (Guid id, DecideProjectQuoteRequest r, ISender sender, CancellationToken ct) =>
                ToResult(await sender.Send(new DecideProjectQuoteCommand(id, r.Approve, r.Comment), ct)))
            .WithName("DecideProjectQuote")
            .WithTags("Opportunities")
            .WithSummary("Approve or refuse a discount beyond the institution's ceiling")
            .WithDescription(
                "A manager (lead:team:supervise) whose team holds the project's owner, or who covers every agency; "
                + "never the owner nor whoever saved the lines (QUOTE_SELF_APPROVAL_FORBIDDEN). A refusal needs a reason.")
            .RequireAuthorization(Permissions.CanReadOpportunities.Code)
            .Accepts<DecideProjectQuoteRequest>("application/json")
            .Produces<ProjectQuoteDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();
        group.MapPost("{id:guid}/quote/issue", async (Guid id, IssueProjectQuoteRequest r, ISender sender, CancellationToken ct) =>
                ToResult(await sender.Send(new IssueProjectQuoteCommand(id, r.ValidityDays, r.PaymentTerms), ct)))
            .WithName("IssueProjectQuote")
            .WithTags("Opportunities")
            .WithSummary("Generate the quote PDF of the offer and file it on the project")
            .WithDescription(
                "The first PDF takes the institution's next number (DEV-2026-000001), later ones a new version of it. "
                + "Refused while the offer is empty (QUOTE_EMPTY) or its discount awaits or failed approval "
                + "(QUOTE_DISCOUNT_NOT_APPROVED). The file is a project attachment (key « quote »): the e-mail and "
                + "WhatsApp sending take it. Requires permission: lead:opportunity:manage.")
            .RequireAuthorization(Permissions.CanManageOpportunities.Code)
            .Accepts<IssueProjectQuoteRequest>("application/json")
            .Produces<ProjectQuoteDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status503ServiceUnavailable)
            .WithOpenApi()
            .WithTenantHeader();
        return group;
    }

    private static IResult ToResult(Result<ProjectQuoteDto> result) => result.IsSuccess
        ? Results.Ok(result.Value)
        : result.Error switch
        {
            OpportunityErrors.NotFound => Results.NotFound(new { error = result.Error }),
            LeadConcurrency.ConflictError => Results.Conflict(new { error = result.Error }),
            ProjectQuoteErrors.ApproverNotAllowed or ProjectQuoteErrors.SelfApproval => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status403Forbidden),
            ProjectQuoteErrors.Unavailable or ProjectFields.ProjectAttachmentHandlers.Unavailable
                => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.BadRequest(new { error = result.Error }),
        };
}
