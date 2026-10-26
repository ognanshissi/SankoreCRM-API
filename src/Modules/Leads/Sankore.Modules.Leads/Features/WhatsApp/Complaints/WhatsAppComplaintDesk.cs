namespace Sankore.Modules.Leads.Features.WhatsApp.Complaints;

using System.Globalization;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Sankore.Modules.Leads.Infrastructure;

/// <summary>What <see cref="WhatsAppComplaintDesk.DecideAsync"/> found: an intake to continue, or one to start (null).</summary>
internal sealed record WhatsAppComplaintTurn(WhatsAppComplaintIntake? Intake);

/// <summary>
/// Takes a complaint on WhatsApp, one question at a time (product owner, 2026-10-26): client
/// number, date of birth, the complaint, its category, a confirmation — then records it on the
/// client's register through <see cref="ICustomersModule.FileComplaintAsync"/>, which assigns it
/// and alerts the advisor.
///
/// <para>
/// <b>No model.</b> The questions are fixed and the answers read by rules: the client number and
/// the date of birth are the two secrets of the remote-KYC check, and the complaint is free text
/// about the person's money — none of it goes to a model provider, and every message the intake
/// consumes is marked <see cref="WhatsAppMessage.ExcludedFromModel"/> so a later bot turn does not
/// send it either. It therefore runs with the conversational bot off.
/// </para>
///
/// <para>
/// <b>Never saves</b>, like <see cref="WhatsAppEscalation"/>: the caller is recording the message
/// that drove the step and commits both together. M01 commits the complaint on its own side.
/// </para>
/// </summary>
internal sealed class WhatsAppComplaintDesk(
    LeadsDbContext db,
    ICustomersModule customers,
    ILeadSettings settings,
    WhatsAppEscalation escalation,
    ILogger<WhatsAppComplaintDesk> logger,
    TimeProvider? clock = null)
{
    internal const string EscalationReason = "BOT_COMPLAINT";

    /// <summary>What starts an intake, matched as whole words after normalisation.</summary>
    internal static readonly IReadOnlyList<string> TriggerPhrases =
    [
        "reclamation", "reclamations", "plainte", "plaintes", "porter plainte", "me plaindre",
        "faire une reclamation", "deposer une reclamation", "complaint",
    ];

    internal static readonly IReadOnlyList<string> CancelWords = ["annuler", "annule", "abandonner", "cancel"];

    /// <summary>The menu the customer picks from, in order — the number is the answer.</summary>
    internal static readonly IReadOnlyList<(string Category, string Label, string[] Words)> Categories =
    [
        ("Credit", "Crédit", ["credit", "pret", "emprunt", "remboursement", "echeance"]),
        ("Savings", "Épargne", ["epargne", "compte", "depot", "retrait"]),
        ("FeesAndPricing", "Frais et tarifs", ["frais", "tarif", "tarifs", "commission", "agios"]),
        ("PaymentMeans", "Moyens de paiement", ["carte", "mobile money", "virement", "paiement", "gab"]),
        ("Service", "Accueil et service", ["accueil", "service", "attente", "agent"]),
        ("DataProtection", "Données personnelles", ["donnees", "donnees personnelles", "confidentialite"]),
        ("Other", "Autre", ["autre"]),
    ];

    private static readonly string[] DateFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "ddMMyyyy"];

    // ── What the customer reads ─────────────────────────────────────────────

    internal const string AskClientNumber =
        "Je vais enregistrer votre réclamation. Pour vous identifier, indiquez votre numéro client "
        + "(il figure dans l'e-mail de bienvenue que vous avez reçu). Écrivez ANNULER à tout moment pour arrêter.";

    internal const string AskBirthDate = "Merci. Indiquez maintenant votre date de naissance (JJ/MM/AAAA).";

    internal const string DateNotUnderstood =
        "Je n'ai pas reconnu la date. Écrivez-la sous la forme JJ/MM/AAAA, par exemple 07/03/1985.";

    internal const string NotIdentified =
        "Ces informations ne nous permettent pas de vous identifier. Vérifiez votre numéro client et indiquez-le à nouveau.";

    internal const string AskDescription =
        "Merci, vous êtes identifié(e). Décrivez votre réclamation en quelques phrases : ce qui s'est passé, "
        + "quand, et le produit concerné.";

    internal const string DescriptionTooShort = "Pouvez-vous décrire votre réclamation en quelques mots de plus ?";

    internal const string AskConfirmation = "Répondez OUI pour enregistrer votre réclamation, ou NON pour l'annuler.";

    internal const string TextOnly = "Je ne peux lire que du texte pour le moment. Merci d'écrire votre réponse.";

    internal const string Cancelled =
        "Votre réclamation n'a pas été enregistrée. Écrivez-nous à tout moment si vous changez d'avis.";

    internal static string AskCategory() =>
        "Votre réclamation concerne :\n"
        + string.Join("\n", Categories.Select((c, i) => $"{i + 1}. {c.Label}"))
        + "\nRépondez par le numéro.";

    internal static string Recap(string label) => $"Récapitulatif : réclamation « {label} ». {AskConfirmation}";

    internal static string Filed(string reference, DateTimeOffset dueAt) =>
        $"Votre réclamation est enregistrée sous la référence {reference}. Votre conseiller vous répondra "
        + $"au plus tard le {dueAt:dd/MM/yyyy}. Merci de votre confiance.";

    // ── Routing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this message belongs to a complaint intake: one is running in the conversation, or
    /// the message asks for one. Null = an ordinary message. A conversation a person holds or awaits
    /// is never captured — the agent takes the complaint down themselves.
    /// </summary>
    public async Task<WhatsAppComplaintTurn?> DecideAsync(
        Guid tenantId, WhatsAppConversation conversation, WhatsAppMessage message, DateTimeOffset now, CancellationToken ct)
    {
        if (conversation.Status is WhatsAppConversationStatus.Human or WhatsAppConversationStatus.PendingHuman) return null;
        if (!await IsEnabledAsync(tenantId, ct)) return null;

        var running = await RunningAsync(conversation.Id, ct);

        if (running is not null)
        {
            if (running.IsActiveAt(now)) return new(running);

            // The customer went quiet: this message is not an answer to a question asked half an hour ago.
            running.End(WhatsAppComplaintOutcome.Expired, reason: null, now);
        }

        return IsTrigger(message) ? new(null) : null;
    }

    public Task<bool> IsEnabledAsync(Guid tenantId, CancellationToken ct) =>
        settings.GetBoolAsync(tenantId, LeadSettingKeys.WhatsAppBotComplaintsEnabled, ct);

    internal static bool IsTrigger(WhatsAppMessage message) =>
        message.Kind is WhatsAppMessageKind.Text or WhatsAppMessageKind.Button
        && WhatsAppBotText.Normalize(message.Body) is { Length: > 0 } normalized
        && TriggerPhrases.Any(p => WhatsAppBotText.ContainsPhrase(normalized, p));

    /// <summary>One step of the intake for the customer's message <paramref name="message"/>.</summary>
    public async Task HandleAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, WhatsAppMessage message,
        WhatsAppComplaintTurn turn, DateTimeOffset now, CancellationToken ct)
    {
        message.ExcludeFromModel();

        if (turn.Intake is not { } intake)
        {
            await StartAsync(tenantId, conversation, lead, now, ct);
            return;
        }

        intake.Touch(now);

        if (message.Kind is not (WhatsAppMessageKind.Text or WhatsAppMessageKind.Button))
        {
            await SayAsync(tenantId, conversation, TextOnly, now, ct);
            return;
        }

        var text = message.Body?.Trim() ?? string.Empty;
        var normalized = WhatsAppBotText.Normalize(text);

        if (CancelWords.Any(w => WhatsAppBotText.ContainsPhrase(normalized, w)))
        {
            intake.End(WhatsAppComplaintOutcome.Cancelled, reason: null, now);
            await SayAsync(tenantId, conversation, Cancelled, now, ct);
            return;
        }

        if (WhatsAppBotRules.HumanRequestPhrases.Any(p => WhatsAppBotText.ContainsPhrase(normalized, p)))
        {
            await HandOverAsync(tenantId, conversation, lead, intake, "HUMAN_REQUESTED",
                "Le client veut déposer une réclamation et a demandé un conseiller.", now, ct);
            return;
        }

        switch (intake.Step)
        {
            case WhatsAppComplaintStep.ClientNumber:
                if (text.Length is 0 or > 40)
                {
                    await SayAsync(tenantId, conversation, AskClientNumber, now, ct);
                    return;
                }

                intake.RecordClientNumber(text);
                await SayAsync(tenantId, conversation, AskBirthDate, now, ct);
                return;

            case WhatsAppComplaintStep.BirthDate:
                await IdentifyAsync(tenantId, conversation, lead, intake, text, now, ct);
                return;

            case WhatsAppComplaintStep.Description:
                if (normalized.Length < 10)
                {
                    await SayAsync(tenantId, conversation, DescriptionTooShort, now, ct);
                    return;
                }

                intake.RecordDescription(text);
                await SayAsync(tenantId, conversation, AskCategory(), now, ct);
                return;

            case WhatsAppComplaintStep.Category:
                if (ParseCategory(normalized) is not { } picked)
                {
                    await SayAsync(tenantId, conversation, AskCategory(), now, ct);
                    return;
                }

                intake.RecordCategory(picked.Category);
                await SayAsync(tenantId, conversation, Recap(picked.Label), now, ct);
                return;

            case WhatsAppComplaintStep.Confirmation:
                await ConfirmAsync(tenantId, conversation, lead, intake, normalized, now, ct);
                return;
        }
    }

    /// <summary>
    /// The bot's MODEL read a complaint in a message no keyword caught: the intake starts there
    /// instead of a handover. False when it cannot — switched off, or too many failed tries today.
    /// </summary>
    public async Task<bool> TryStartFromBotAsync(
        Guid tenantId, WhatsAppConversation conversation, WhatsAppMessage trigger, DateTimeOffset now, CancellationToken ct)
    {
        if (!await IsEnabledAsync(tenantId, ct)) return false;
        if (await RunningAsync(conversation.Id, ct) is { } running && running.IsActiveAt(now)) return false;
        if (await FailuresInWindowAsync(conversation.Id, now, ct) >= WhatsAppComplaintIntake.MaxFailedIdentifications) return false;

        trigger.ExcludeFromModel();
        db.WhatsAppComplaintIntakes.Add(WhatsAppComplaintIntake.Start(tenantId, conversation.Id, now));
        await SayAsync(tenantId, conversation, AskClientNumber, now, ct);
        return true;
    }

    // ── Steps ───────────────────────────────────────────────────────────────

    private async Task StartAsync(Guid tenantId, WhatsAppConversation conversation, Lead? lead, DateTimeOffset now, CancellationToken ct)
    {
        // The bound is per conversation, not per intake: restarting must not buy fresh tries at a
        // client number and a date of birth.
        if (await FailuresInWindowAsync(conversation.Id, now, ct) >= WhatsAppComplaintIntake.MaxFailedIdentifications)
        {
            await escalation.EscalateAsync(tenantId, conversation, lead, EscalationReason,
                "Le client veut déposer une réclamation ; trop d'échecs d'identification aujourd'hui pour la prendre automatiquement.",
                now, profile: null, tellCustomer: true, ct);
            return;
        }

        db.WhatsAppComplaintIntakes.Add(WhatsAppComplaintIntake.Start(tenantId, conversation.Id, now));
        await SayAsync(tenantId, conversation, AskClientNumber, now, ct);
    }

    private async Task IdentifyAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, WhatsAppComplaintIntake intake,
        string text, DateTimeOffset now, CancellationToken ct)
    {
        if (ParseBirthDate(text, now) is not { } birthDate)
        {
            await SayAsync(tenantId, conversation, DateNotUnderstood, now, ct);
            return;
        }

        Guid? clientId;

        try
        {
            using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            clientId = await customers.IdentifyClientAsync(tenantId, intake.PendingClientNumber ?? string.Empty, birthDate, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("WhatsApp complaint: the client lookup failed ({Reason}); handed to a person.", ex.GetType().Name);
            await HandOverAsync(tenantId, conversation, lead, intake, "IDENTIFICATION_UNAVAILABLE",
                "Le client veut déposer une réclamation ; l'identification automatique est indisponible.", now, ct);
            return;
        }

        if (clientId is { } identified)
        {
            intake.RecordIdentified(identified);
            conversation.IdentifyClient(identified);
            await SayAsync(tenantId, conversation, AskDescription, now, ct);
            return;
        }

        intake.RecordIdentificationFailed();

        if (await FailuresInWindowAsync(conversation.Id, now, ct) >= WhatsAppComplaintIntake.MaxFailedIdentifications)
        {
            await HandOverAsync(tenantId, conversation, lead, intake, "IDENTIFICATION_FAILED",
                $"Réclamation : le client n'a pas pu être identifié ({WhatsAppComplaintIntake.MaxFailedIdentifications} essais).", now, ct);
            return;
        }

        await SayAsync(tenantId, conversation, NotIdentified, now, ct);
    }

    private async Task ConfirmAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, WhatsAppComplaintIntake intake,
        string normalized, DateTimeOffset now, CancellationToken ct)
    {
        if (normalized is "non" or "no" or "n")
        {
            intake.End(WhatsAppComplaintOutcome.Cancelled, reason: null, now);
            await SayAsync(tenantId, conversation, Cancelled, now, ct);
            return;
        }

        if (normalized is not ("oui" or "o" or "yes" or "ok" or "confirmer" or "je confirme" or "oui je confirme"))
        {
            await SayAsync(tenantId, conversation, AskConfirmation, now, ct);
            return;
        }

        var label = Categories.FirstOrDefault(c => c.Category == intake.Category).Label ?? "Autre";

        try
        {
            using var suppress = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            var filed = await customers.FileComplaintAsync(new FileClientComplaintRequest(
                tenantId, intake.ClientId!.Value, intake.Category ?? "Other",
                // The subject is in clear in lists and e-mails: the category, never the customer's words.
                $"Réclamation WhatsApp — {label}", intake.Description ?? string.Empty,
                SourceConversationId: conversation.Id), ct);

            if (filed.IsSuccess)
            {
                intake.RecordFiled(filed.Value!.ComplaintId, filed.Value.Reference, now);
                await SayAsync(tenantId, conversation, Filed(filed.Value.Reference, filed.Value.ResolutionDueAt), now, ct);
                return;
            }

            await HandOverAsync(tenantId, conversation, lead, intake, filed.Error,
                $"Réclamation « {label} » : l'enregistrement automatique a été refusé ({filed.Error}). Prenez-la en charge.", now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "WhatsApp complaint of conversation {ConversationId} could not be filed.", conversation.Id);
            await HandOverAsync(tenantId, conversation, lead, intake, "FILING_FAILED",
                $"Réclamation « {label} » : l'enregistrement automatique a échoué. Prenez-la en charge.", now, ct);
        }
    }

    private async Task HandOverAsync(
        Guid tenantId, WhatsAppConversation conversation, Lead? lead, WhatsAppComplaintIntake intake,
        string? reason, string summary, DateTimeOffset now, CancellationToken ct)
    {
        intake.End(WhatsAppComplaintOutcome.Escalated, reason, now);
        await escalation.EscalateAsync(tenantId, conversation, lead, EscalationReason, summary, SendTime(now), profile: null, tellCustomer: true, ct);
    }

    private Task SayAsync(Guid tenantId, WhatsAppConversation conversation, string text, DateTimeOffset now, CancellationToken ct) =>
        escalation.QueueAutomaticAsync(tenantId, conversation, WhatsAppAuthor.Bot, text, SendTime(now), ct);

    /// <summary>
    /// The instant a reply is queued, which is also the order the customer receives it in. Read
    /// NOW and not at the start of the delivery: two messages typed seconds apart can be processed
    /// together (each delivery is its own Hangfire job), and stamped with the handler's start the
    /// second one's « un conseiller vous répond » went out BEFORE the first one's « indiquez votre
    /// date de naissance » — seen in recette, 2026-10-26.
    /// </summary>
    private DateTimeOffset SendTime(DateTimeOffset now) => clock is null ? now : Max(now, clock.GetUtcNow());

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    // ── Reading ─────────────────────────────────────────────────────────────

    private async Task<WhatsAppComplaintIntake?> RunningAsync(Guid conversationId, CancellationToken ct) =>
        db.WhatsAppComplaintIntakes.Local.FirstOrDefault(i => i.ConversationId == conversationId && i.Step != WhatsAppComplaintStep.Ended)
        ?? await db.WhatsAppComplaintIntakes
            .AsTracking()
            .Where(i => i.ConversationId == conversationId && i.Step != WhatsAppComplaintStep.Ended)
            .OrderByDescending(i => i.StartedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>Failed identifications of this conversation over the last day, the intake in hand included.</summary>
    private async Task<int> FailuresInWindowAsync(Guid conversationId, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - WhatsAppComplaintIntake.AttemptWindow;
        var tracked = db.WhatsAppComplaintIntakes.Local
            .Where(i => i.ConversationId == conversationId && i.LastActivityAt >= since)
            .ToList();
        var trackedIds = tracked.Select(i => i.Id).ToList();

        var stored = await db.WhatsAppComplaintIntakes
            .Where(i => i.ConversationId == conversationId && i.LastActivityAt >= since && !trackedIds.Contains(i.Id))
            .SumAsync(i => i.FailedIdentifications, ct);

        return stored + tracked.Sum(i => i.FailedIdentifications);
    }

    internal static DateOnly? ParseBirthDate(string text, DateTimeOffset now)
    {
        var trimmed = text.Trim();
        if (!DateOnly.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return null;

        // A typo that parses (07/03/2085) is not a birth date.
        return date.Year >= 1900 && date <= DateOnly.FromDateTime(now.UtcDateTime) ? date : null;
    }

    internal static (string Category, string Label)? ParseCategory(string normalized)
    {
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= Categories.Count)
            return (Categories[n - 1].Category, Categories[n - 1].Label);

        foreach (var c in Categories)
            if (c.Words.Any(w => WhatsAppBotText.ContainsPhrase(normalized, w)))
                return (c.Category, c.Label);

        return null;
    }
}
