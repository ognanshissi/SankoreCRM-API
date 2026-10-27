namespace Sankore.Modules.Leads.Domain;

using System.Text.Json;
using System.Text.Json.Serialization;
using Sankore.Shared.Kernel;

/// <summary>
/// What a project template said at one point (product owner, 2026-10-26): its steps, their
/// conditions, its fields and its forecast settings, frozen. A project is attached to the version
/// it was created with, so editing the template changes what NEW projects follow and leaves the
/// running ones alone until somebody moves them onto the latest version.
/// </summary>
/// <remarks>
/// <para>
/// <b>A save makes a version only when the latest one is in use.</b> A template saved five times
/// before any project picks it up keeps one version, rewritten in place; the first project
/// attached to it freezes it, and the next save opens the following number.
/// </para>
/// <para>
/// <b>Version 1 of a template that existed before versions is its state BEFORE its first save
/// since then</b> (<see cref="IsBaseline"/>). Projects created before versions carry no version
/// and read that one — the rule « the projects in progress stay on version 1 = the state at the
/// time » with no start-up backfill, which would have had to resolve every project's product
/// through the catalogue before the host serves a request. A baseline is always in use.
/// </para>
/// </remarks>
public sealed class ProjectTemplateVersion : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TemplateId { get; private set; }

    /// <summary>1, 2, 3… per template.</summary>
    public int Number { get; private set; }

    /// <summary>The version 1 written for the projects that existed before versions.</summary>
    public bool IsBaseline { get; private set; }

    /// <summary>The template's content, as <see cref="ProjectTemplateSnapshot"/> JSON.</summary>
    public string Snapshot { get; private set; } = default!;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ProjectTemplateVersion() { }

    internal static ProjectTemplateVersion Create(
        ProjectTemplate template, int number, DateTimeOffset now, bool baseline = false) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = template.TenantId,
        TemplateId = template.Id,
        Number = number,
        IsBaseline = baseline,
        Snapshot = ProjectTemplateSnapshot.Of(template).ToJson(),
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>Rewrites a version no project uses yet.</summary>
    internal void Rewrite(string snapshot, DateTimeOffset now)
    {
        Snapshot = snapshot;
        UpdatedAt = now;
    }

    public ProjectTemplateSnapshot Read() => ProjectTemplateSnapshot.FromJson(Snapshot);
}

/// <summary>Everything a project reads from its template.</summary>
public sealed record ProjectTemplateSnapshot(
    string Name,
    IReadOnlyList<ProjectTemplateSnapshotStep> Steps,
    bool TracksForecast,
    ProjectAmountKind AmountKind,
    bool CriticalCloseDate,
    IReadOnlyList<ProjectFieldDefinition> Fields)
{
    // Enums by name: a version is read years later, and an enum renumbered in between must not
    // move a project to another phase.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static ProjectTemplateSnapshot Of(ProjectTemplate template) => new(
        template.Name,
        template.Steps.Select(s => new ProjectTemplateSnapshotStep(
            s.Id, s.Label, s.Phase, s.Probability, s.Suggestion, s.OpensClientFile, s.IsApproval,
            s.RequiresQuestionnaires, s.RequiresAmount, s.RequiresCloseDate, s.Checklist, s.WorkflowCircuit,
            s.RequiredFieldKeys)).ToList(),
        template.TracksForecast,
        template.AmountKind,
        template.CriticalCloseDate,
        template.Fields);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ProjectTemplateSnapshot FromJson(string json)
        => JsonSerializer.Deserialize<ProjectTemplateSnapshot>(json, Json)
           ?? throw new InvalidOperationException("Empty project template snapshot.");
}

public sealed record ProjectTemplateSnapshotStep(
    Guid Id,
    string Label,
    OpportunityStage Phase,
    int Probability,
    string? Suggestion,
    bool OpensClientFile,
    bool IsApproval,
    bool RequiresQuestionnaires,
    bool RequiresAmount,
    bool RequiresCloseDate,
    IReadOnlyList<ProjectChecklistItem> Checklist,
    string? WorkflowCircuit,
    IReadOnlyList<string> RequiredFieldKeys);
