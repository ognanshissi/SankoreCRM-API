namespace Sankore.Modules.Leads.Features.Questionnaires;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Modules.Leads.PublicApi;

/// <summary>The facade's handle on <see cref="ProjectAnswersSnapshotBuilder"/>.</summary>
public interface IProjectAnswersSnapshots
{
    Task<ProjectAnswersSnapshot?> BuildAsync(Guid tenantId, Guid opportunityId, CancellationToken ct);

    Task MarkTransmittedAsync(Guid tenantId, Guid opportunityId, DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// The answers of a project as an insurance subscription carries them to the insurer.
///
/// <para>
/// Inside <c>Features/Questionnaires</c> because it reads health answers in clear, and
/// <see cref="HealthAnswerAccess"/> is the only door to them. It is opened with the health
/// permission set because the reader is not a person browsing a screen: it is the subscription the
/// client signed, and a declaration the insurer never receives is one it cannot rely on.
/// </para>
///
/// <para>
/// The text is CANONICAL — every expected questionnaire, in a fixed order, the same field names —
/// so its SHA-256 says whether a later reading is the same declaration. The subscription compares
/// the two just before sending, which is what catches an answer edited between the signature and
/// the transmission.
/// </para>
/// </summary>
internal sealed class ProjectAnswersSnapshotBuilder(
    LeadsDbContext db,
    QuestionnaireCompletenessChecker checker) : IProjectAnswersSnapshots
{
    public async Task<ProjectAnswersSnapshot?> BuildAsync(
        Guid tenantId, Guid opportunityId, CancellationToken ct)
    {
        var project = await db.Opportunities
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Id == opportunityId, ct);

        if (project is null)
            return null;

        var lead = await checker.LeadOfAsync(project, ct);
        var states = await checker.StatesAsync(project, lead, ct);

        if (states.Count == 0)
            return null;

        var templateIds = states.Select(s => s.Expected.TemplateId).Distinct().ToList();
        var names = await db.QualificationTemplates
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var entries = states
            .OrderBy(s => s.Expected.TemplateId)
            .ThenBy(s => s.Expected.SubjectType)
            .ThenBy(s => s.Expected.SubjectId)
            .Select(s =>
            {
                var answers = s.IsAnswered && s.Response is { } response
                    && HealthAnswerAccess.Read(response, callerHoldsHealthPermission: true) is { IsSuccess: true } read
                        ? read.Value!.Answers
                            .OrderBy(a => a.QuestionId)
                            .Select(a => new SnapshotAnswer(a.QuestionId, a.Value))
                            .ToList()
                        : [];

                return new SnapshotEntry(
                    TemplateId: s.Expected.TemplateId,
                    Template: names.TryGetValue(s.Expected.TemplateId, out var name) ? name : "—",
                    Version: s.Response?.QuestionnaireVersion ?? s.Expected.TemplateVersion,
                    SubjectType: s.Expected.SubjectType.ToString(),
                    SubjectId: s.Expected.SubjectId,
                    Subject: QuestionnaireSubjectLabel.For(lead, s.Expected.SubjectType, s.Expected.SubjectId),
                    Health: s.Expected.IsHealthData,
                    Answered: s.IsAnswered,
                    AnsweredAt: s.IsAnswered ? s.Response?.AnsweredAt : null,
                    Answers: answers);
            })
            .ToList();

        var json = JsonSerializer.Serialize(entries, Canonical);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var missing = entries.Count(e => !e.Answered);

        return new ProjectAnswersSnapshot(
            IsComplete: missing == 0,
            MissingCount: missing,
            ContainsHealthData: entries.Any(e => e.Health),
            AnswersJson: json,
            Sha256: hash);
    }

    /// <summary>
    /// Every response the snapshot was built from, lead-level ones included: an answer given on
    /// the lead and inherited by the project is part of what the insurer received.
    /// </summary>
    public async Task MarkTransmittedAsync(Guid tenantId, Guid opportunityId, DateTimeOffset now, CancellationToken ct)
    {
        var project = await db.Opportunities
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Id == opportunityId, ct);

        if (project is null)
            return;

        var lead = await checker.LeadOfAsync(project, ct);
        var states = await checker.StatesAsync(project, lead, ct);

        var ids = states
            .Where(s => s.IsAnswered && s.Response is not null)
            .Select(s => s.Response!.Id)
            .ToList();

        if (ids.Count == 0)
            return;

        var responses = await db.QualificationResponses
            .AsTracking()
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(ct);

        foreach (var response in responses)
            response.MarkTransmitted(now);

        await db.SaveChangesAsync(ct);
    }

    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private sealed record SnapshotEntry(
        Guid TemplateId,
        string Template,
        int Version,
        string SubjectType,
        Guid SubjectId,
        string Subject,
        bool Health,
        bool Answered,
        DateTimeOffset? AnsweredAt,
        IReadOnlyList<SnapshotAnswer> Answers);

    private sealed record SnapshotAnswer(Guid QuestionId, string Value);
}
