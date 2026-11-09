using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.Appointments;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.CloseLead;
using Sankore.Modules.Leads.Features.LogActivity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunity;
using Sankore.Modules.Leads.Features.Opportunities.CreateOpportunityForCustomer;
using Sankore.Modules.Leads.Features.Opportunities.MoveToStep;
using Sankore.Modules.Leads.Features.Opportunities.ProjectFields;
using Sankore.Modules.Leads.Features.Opportunities.StepConditions;
using Sankore.Modules.Leads.Features.ProjectTemplates;
using Sankore.Modules.Leads.Features.QualifyLead;
using Sankore.Modules.Leads.Features.RecordFirstContact;
using Sankore.Modules.Leads.Features.Tasks.CreateTask;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Infrastructure.BackgroundJobs;
using Sankore.Shared.Infrastructure.Tenants;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Features.StartingData;

/// <summary>
/// The demo's commercial activity (<c>docs/donnees-demarrage-demo.md</c>, lot 4): forty-eight leads
/// at every stage, projects on qualified leads, credit files on clients advanced up to the analyst's
/// queue, today's tasks and this week's appointments. Each act is done AS the agent who would have
/// done it (<see cref="BackgroundJobContext"/> around a command sent in a scope of its own), so
/// histories, owners and queues read as they would after a real week. Idempotent on the lead's
/// « DEMO-L… » external reference and on the titles of projects, tasks and appointments; dates are
/// spread over the last weeks at the end, so reports have a past to compare with.
/// </summary>
internal sealed class LeadsDemoData : ITenantStartingData
{
    public int Order => 60;
    public string Module => "Activité commerciale de démo";

    private static readonly string[] FirstNames =
        ["Adama", "Aïcha", "Brice", "Clarisse", "Daouda", "Estelle", "Fabrice", "Gisèle", "Hamed", "Inès", "Jules", "Kadiatou",
         "Lassina", "Marina", "Noël", "Oumou", "Pascal", "Rachel", "Sékou", "Tatiana", "Ulrich", "Victoire", "Wilfried", "Yasmine"];

    private static readonly string[] LastNames =
        ["Kouadio", "Coulibaly", "Bamba", "Koffi", "Diomandé", "Touré", "N'Dri", "Gnamien", "Ouattara", "Yao", "Soro", "Kacou"];

    /// <summary>Catalogue names, as an agent types them; « Crédit » ones carry an amount.</summary>
    private static readonly string[] Products =
        ["Crédit individuel", "Compte d'épargne", "Assurance santé", "Compte courant", "Crédit agricole de campagne", "Prévoyance décès", "Tontine"];

    /// <summary>The agents who receive leads, by branch.</summary>
    private static readonly string[] Agents =
        ["agent-plateau-1", "agent-plateau-2", "agent-commercial-plateau", "agent-yopougon-1", "agent-yopougon-2", "agent-commercial-yopougon"];

    private static readonly (LeadSource Source, string Code)[] Sources =
        [(LeadSource.Agency, "AGENCE"), (LeadSource.Referral, "PARRAINAGE"), (LeadSource.CallCenter, "APPEL-ENTRANT"), (LeadSource.Web, "SITE-WEB"), (LeadSource.FileImport, "IMPORT-FICHIER")];

    internal const int LeadCount = 48;

    /// <summary>What happens to lead <c>i</c> — every eighth one waits for the dispatch.</summary>
    internal enum Fate { New, Contacted, QualifiedWithProject, Qualified, Lost, Disqualified, Unassigned }

    internal static Fate FateOf(int i) => (i % 8) switch
    {
        0 or 1 => Fate.New,
        2 or 3 => Fate.Contacted,
        4 => Fate.QualifiedWithProject,
        5 => Fate.Qualified,
        6 => i % 16 == 6 ? Fate.Lost : Fate.Disqualified,
        _ => Fate.Unassigned,
    };

    public async Task<IReadOnlyDictionary<string, int>> LoadAsync(StartingDataRequest request, IServiceProvider services, CancellationToken ct)
    {
        var created = new Dictionary<string, int>();
        if (request.Kind != StartingDataKind.Demo) return created;

        var db = services.GetRequiredService<LeadsDbContext>();
        var sender = services.GetRequiredService<ISender>();
        var templates = services.GetRequiredService<ProjectTemplateResolver>();
        var now = DateTimeOffset.UtcNow;

        Guid Account(string profile) => request.Shared.TryGetValue(DemoKeys.Account(profile), out var id) ? id
            : throw new InvalidOperationException($"Compte « {profile} » introuvable : les comptes de démo doivent être chargés avant l'activité.");
        Guid Agency(string name) => request.Shared[DemoKeys.Agency(name)];
        var sourceIds = await db.LeadSourceConfigs.ToDictionaryAsync(s => s.Code, s => s.Id, ct);

        // ── Leads ────────────────────────────────────────────────────────────
        var existing = await db.Leads.Where(l => l.ExternalReference != null && l.ExternalReference.StartsWith("DEMO-L"))
            .ToDictionaryAsync(l => l.ExternalReference!, l => l.Id, ct);
        var leadIds = new Dictionary<int, Guid>();
        int leads = 0, projects = 0;
        for (var i = 0; i < LeadCount; i++)
        {
            var reference = $"DEMO-L{i + 1:D3}";
            if (existing.TryGetValue(reference, out var known)) { leadIds[i] = known; continue; }

            var fate = FateOf(i);
            var first = FirstNames[i % FirstNames.Length];
            var last = LastNames[(i * 7 + i / FirstNames.Length) % LastNames.Length];
            var product = Products[i % Products.Length];
            var (source, code) = Sources[i % Sources.Length];
            var ownerProfile = Agents[i % Agents.Length];
            var owner = fate == Fate.Unassigned ? (Guid?)null : Account(ownerProfile);
            var branch = ownerProfile.Contains("yopougon") ? "Agence Yopougon" : "Agence Plateau";
            var amount = product.StartsWith("Crédit", StringComparison.Ordinal) ? 500_000m + (i % 6) * 250_000m : (decimal?)null;

            var captured = Ok(await sender.Send(new CaptureLeadCommand(
                request.TenantId, $"{first} {last}", Phone(i + 1), source, product, "fr", 5.3364, -4.0267, Agency(branch),
                FirstName: first, LastName: last, Email: $"{Slug(first)}.{Slug(last)}{i + 1}@example.com",
                DesiredAmount: amount, DesiredCurrency: amount is null ? null : "XOF",
                Comment: "Prospect de démonstration.", ExternalReference: reference, OwnerId: owner, AgencyId: Agency(branch),
                Force: true, LeadSourceConfigId: sourceIds.GetValueOrDefault(code)), ct), reference);
            var leadId = captured.LeadId!.Value;
            leadIds[i] = leadId;
            leads++;

            if (owner is not { } agent || fate == Fate.New) continue;

            // What the agent did since: always a first call, then the lead's own fate.
            var contactedAt = now.AddDays(-(2 + i % 9));
            Ok(await As(request, agent, () => sender.Send(new RecordFirstContactCommand(leadId, contactedAt), ct)), reference);
            Ok(await As(request, agent, () => sender.Send(new LogActivityCommand(leadId, ActivityType.Call, "Appel de découverte",
                $"Besoin confirmé : {product.ToLowerInvariant()}. Rappel convenu.", contactedAt, 8, ActivityOutcome.Reached), ct)), reference);

            switch (fate)
            {
                case Fate.Qualified:
                    Ok(await As(request, agent, () => sender.Send(new QualifyLeadCommand(leadId, agent, 72), ct)), reference);
                    break;
                case Fate.QualifiedWithProject:
                    Ok(await As(request, agent, () => sender.Send(new CreateOpportunityCommand(request.TenantId, leadId,
                        $"{product} — {first} {last}", product, agent, "Projet de démonstration.", amount ?? 0m, "XOF",
                        now.AddDays(20 + i % 10)), ct)), reference);
                    projects++;
                    break;
                case Fate.Lost:
                    Ok(await As(request, agent, () => sender.Send(new CloseLeadCommand(leadId, LeadCloseReason.Lost, "Taux jugé trop élevé."), ct)), reference);
                    break;
                case Fate.Disqualified:
                    Ok(await As(request, agent, () => sender.Send(new CloseLeadCommand(leadId, LeadCloseReason.Disqualified, "Hors zone d'intervention."), ct)), reference);
                    break;
            }
        }
        if (leads > 0) created["Leads"] = leads;

        // ── Credit files on clients, at three steps ───────────────────────────
        var titles = (await db.Opportunities.Select(o => o.Title).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var creditManager = Account("gestionnaire-credit");
        foreach (var (clientKey, name, amount, target) in CreditFiles)
        {
            var title = $"Crédit individuel — {name}";
            if (titles.Contains(title) || !request.Shared.TryGetValue(DemoKeys.Client(clientKey), out var clientId)) continue;

            Ok(await As(request, creditManager, () => sender.Send(new CreateOpportunityForCustomerCommand(
                request.TenantId, clientId, title, "Crédit individuel", creditManager, Description: "Dossier de crédit de démonstration.",
                EstimatedAmount: amount, EstimatedCurrency: "XOF", ExpectedCloseDate: now.AddDays(30), Force: true), ct)), title);
            projects++;
            if (target == 0) continue;

            var project = await db.Opportunities.AsNoTracking().FirstAsync(o => o.Title == title, ct);
            var steps = (await templates.ForProjectAsync(project, ct)).Steps ?? [];
            var file = steps.FirstOrDefault(s => s.Label == "Constitution du dossier");
            var analysis = steps.FirstOrDefault(s => s.Label == "Analyse et décision");
            if (file is null) continue;

            await As(request, creditManager, () => sender.Send(new SetProjectFieldsCommand(project.Id, new Dictionary<string, JsonElement>
            {
                [CreditFileTemplate.TermFieldKey] = JsonSerializer.SerializeToElement(24),
                [CreditFileTemplate.PurposeFieldKey] = JsonSerializer.SerializeToElement("Fonds de roulement de l'activité"),
            }), ct));
            // A refused move leaves the file where it stands: the demo shows it there.
            if ((await As(request, creditManager, () => sender.Send(new MoveOpportunityToStepCommand(project.Id, file.Id), ct))).IsFailure
                || target == 1 || analysis is null)
                continue;
            foreach (var item in file.Checklist ?? [])
                await As(request, creditManager, () => sender.Send(new SetProjectChecklistItemCommand(project.Id, item.Id, true), ct));
            await As(request, creditManager, () => sender.Send(new MoveOpportunityToStepCommand(project.Id, analysis.Id), ct));
        }

        // ── Tasks and appointments ────────────────────────────────────────────
        var taskTitles = (await db.CrmTasks.Select(t => t.Title).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var backdated = new Dictionary<Guid, DateTimeOffset>();
        int tasks = 0, appointments = 0;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        // On leads still being worked — contacted or qualified, all owned — spread over every agent.
        var worked = Enumerable.Range(0, LeadCount)
            .Where(i => FateOf(i) is Fate.Contacted or Fate.Qualified or Fate.QualifiedWithProject)
            .ToList();
        for (var t = 0; t < 12; t++)
        {
            var i = worked[t % worked.Count];
            if (!leadIds.TryGetValue(i, out var leadId)) continue;
            var agentProfile = Agents[i % Agents.Length];
            var title = $"Rappeler {FirstNames[i % FirstNames.Length]} {LastNames[(i * 7 + i / FirstNames.Length) % LastNames.Length]} — {Products[i % Products.Length]}";
            if (!taskTitles.Add(title)) continue;
            var due = (t % 4) switch
            {
                0 => today.AddHours(10),          // this morning
                1 => today.AddHours(16),          // this afternoon
                2 => today.AddDays(-1).AddHours(11), // overdue
                _ => today.AddDays(1 + t % 3).AddHours(9),
            };
            var manager = Account(agentProfile.Contains("yopougon") ? "chef-agence-yopougon" : "chef-agence-plateau");
            var agent = Account(agentProfile);
            // A task is created due in the future (the validator refuses a past date); the ones meant
            // to be late or due this morning get their date back with the rest of the past, below.
            var createdDue = due > now.AddMinutes(10) ? due : now.AddHours(1);
            var taskId = Ok(await As(request, manager, () => sender.Send(new CreateTaskCommand(CrmTaskType.Generic,
                t % 4 == 2 ? CrmTaskPriority.High : CrmTaskPriority.Medium, title, createdDue, leadId, agent,
                Description: "Faire le point sur le besoin et proposer un rendez-vous."), ct)), title);
            if (createdDue != due) backdated[taskId] = due;
            tasks++;

            if (t % 3 != 0) continue;
            var meeting = $"Rendez-vous en agence — {title[9..]}";
            if (!taskTitles.Add(meeting)) continue;
            var startAt = today.AddDays(1 + t % 4).AddHours(10 + t % 5);
            var appointment = await As(request, agent, () => sender.Send(new ScheduleAppointmentCommand(new ScheduleAppointmentRequest(
                meeting, startAt, 45, "Africa/Abidjan", AppointmentMode.InPerson,
                agentProfile.Contains("yopougon") ? "Agence Yopougon" : "Agence Plateau", null, leadId, null, [agent],
                "Présentation de l'offre et des pièces à fournir.")), ct));
            if (appointment.IsSuccess) appointments++;
        }
        if (projects > 0) created["Projets"] = projects;
        if (tasks > 0) created["Tâches"] = tasks;
        if (appointments > 0) created["Rendez-vous"] = appointments;

        // ── A past for the reports ────────────────────────────────────────────
        // Captured over the last twelve weeks, the oldest first; leads still « new » arrived this week.
        // Direct writes of timestamps only, on the demo's own rows: no command dates a lead in the past.
        foreach (var (i, leadId) in leadIds)
        {
            var capturedAt = FateOf(i) is Fate.New or Fate.Unassigned
                ? now.AddHours(-(6 + i % 60))
                : now.AddDays(-(12 + (LeadCount - i) * 3 % 75));
            await db.Leads.Where(l => l.Id == leadId && l.CapturedAt > capturedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.CapturedAt, capturedAt).SetProperty(l => l.CreatedAt, capturedAt), ct);
        }

        foreach (var (taskId, due) in backdated)
            await db.CrmTasks.Where(t => t.Id == taskId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.DueAt, due), ct);

        return created;
    }

    /// <summary>Client key (from the clients' step), name, amount, how far: 0 Demande, 1 Constitution, 2 Analyse.</summary>
    internal static readonly IReadOnlyList<(string Client, string Name, decimal Amount, int Target)> CreditFiles =
    [
        ("seydou-traore", "Seydou Traoré", 2_500_000m, 0),
        ("awa-kone", "Awa Koné", 1_500_000m, 1),
        ("bakary-diarra", "Bakary Diarra", 800_000m, 2),
        ("fanta-camara", "Fanta Camara", 3_000_000m, 2),
    ];

    /// <summary>A Côte d'Ivoire mobile number reserved for the demo's prospects: +225 07 98 0x xx xx.</summary>
    internal static string Phone(int n) => $"+22507980{n:D5}";

    private static string Slug(string name) => new string(name.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => c < 128 && char.IsLetter(c)).ToArray()).ToLowerInvariant();

    /// <summary>Runs <paramref name="work"/> as <paramref name="userId"/>: the command's scope is built from it.</summary>
    private static async Task<T> As<T>(StartingDataRequest request, Guid userId, Func<Task<T>> work)
    {
        using (BackgroundJobContext.SetScope(request.TenantId, userId, "Démo"))
            return await work();
    }

    private static T Ok<T>(Result<T> result, string what)
        => result.IsSuccess ? result.Value! : throw new InvalidOperationException($"{what} : {result.Error}");

    private static void Ok(Result result, string what)
    {
        if (result.IsFailure) throw new InvalidOperationException($"{what} : {result.Error}");
    }
}
