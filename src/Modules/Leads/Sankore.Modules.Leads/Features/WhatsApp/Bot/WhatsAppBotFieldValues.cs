namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using System.Globalization;
using System.Text.Json;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// What a bot turn needs to read the fields: the catalogue (the product's choices and its name)
/// and the tenant's lead form (the custom fields). The form is null when it could not be read —
/// the custom fields are then neither offered nor written, the native ones go on.
/// </summary>
internal sealed record BotFieldContext(IReadOnlyList<ProductSummary> Catalogue, EntityFormDto? LeadForm)
{
    /// <summary>The form's custom fields the bot may collect: live, not sensitive, not a reference to another record.</summary>
    public FormField? CustomField(string code) =>
        WhatsAppBotFields.IsCustom(code)
            ? LeadForm?.Fields.FirstOrDefault(f => !f.IsNative && f.Key == WhatsAppBotFields.CustomKey(code) && IsCollectable(f))
            : null;

    /// <summary>
    /// Sensitive values are masked on every screen that shows them — a chat transcript is not the
    /// place to collect one; a reference field holds an id no customer can type.
    /// </summary>
    public static bool IsCollectable(FormField f) =>
        !f.IsNative && !f.IsArchived && !f.IsSensitive && f.ReferenceTarget is null;

    public string LabelOf(string code) => CustomField(code)?.Label ?? WhatsAppBotFields.LabelOf(code);
}

/// <summary>
/// The fields the bot collects, in two steps that the deferred lead needs kept apart:
/// <see cref="Normalize"/> turns what the model wrote into the value to keep — refusing what the
/// lead would refuse — and <see cref="Write"/> puts a kept value on a lead. With a lead, the two run
/// back to back; without one, the normalised value waits on the conversation and is written when
/// the lead is created, by the same <see cref="Write"/>, so a pending value and a direct one cannot
/// end up different on the record.
/// </summary>
internal sealed class WhatsAppBotFieldValues(
    ILeadEmailIndexer emailIndexer,
    IAdministrationModule admin,
    TimeProvider clock,
    ILogger<WhatsAppBotFieldValues> logger)
{
    /// <summary>The catalogue and the lead form, read once per turn. Neither failing stops the turn.</summary>
    public async Task<BotFieldContext> ContextAsync(Guid tenantId, CancellationToken ct)
    {
        IReadOnlyList<ProductSummary> catalogue = [];
        EntityFormDto? form = null;

        try
        {
            catalogue = await admin.ListProductsAsync(tenantId, null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The FAQ still answers; only the product's name and choices are missing.
            logger.LogWarning("WhatsApp bot: the product catalogue could not be read ({Reason}).", ex.GetType().Name);
        }

        try
        {
            // M12 reads through its own connection; never enlisted in a caller's transaction.
            using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
            form = await admin.GetEntityFormAsync(tenantId, FormEntity.Lead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("WhatsApp bot: the lead form could not be read ({Reason}); custom fields skipped.", ex.GetType().Name);
        }

        return new BotFieldContext(catalogue, form);
    }

    /// <summary>
    /// The value to keep, or the refusal in words the logs can carry. <paramref name="lead"/> is
    /// null while no lead exists; <paramref name="pending"/> is what the conversation already holds.
    /// </summary>
    public (string? Value, string? Refusal) Normalize(
        string code, string raw, Lead? lead, IReadOnlyDictionary<string, string> pending, BotFieldContext ctx)
    {
        var value = raw.Trim();

        switch (code)
        {
            case WhatsAppBotFields.FirstName:
            case WhatsAppBotFields.LastName:
            case WhatsAppBotFields.City:
            case WhatsAppBotFields.Neighbourhood:
                return (value, null);

            case WhatsAppBotFields.Email:
                return System.Net.Mail.MailAddress.TryCreate(value, out var address) && address.Host.Contains('.')
                    ? (address.Address, null)
                    : (null, "Adresse e-mail invalide.");

            case WhatsAppBotFields.Gender:
                return WhatsAppBotText.Normalize(value) switch
                {
                    "homme" or "masculin" or "m" or "male" or "man" => (nameof(LeadGender.Male), null),
                    "femme" or "feminin" or "f" or "female" or "woman" => (nameof(LeadGender.Female), null),
                    _ => (null, "Genre attendu : Homme ou Femme."),
                };

            case WhatsAppBotFields.DesiredAmount:
                return WhatsAppBotEngine.ParseAmount(value) is { } amount
                    ? ($"{amount.Amount.ToString(CultureInfo.InvariantCulture)} {amount.Currency}", null)
                    : (null, "Montant illisible.");

            case WhatsAppBotFields.Product:
            {
                // Never overwrites: a product an agent resolved, or the bot an earlier turn, stays.
                if (LineOf(lead, ctx.Catalogue) is not null || pending.ContainsKey(WhatsAppBotFields.Product))
                    return (null, "Le produit est déjà connu.");

                var wanted = WhatsAppBotText.Normalize(value);
                var matches = ctx.Catalogue
                    .Where(p => p.IsActive
                             && (WhatsAppBotText.Normalize(p.Name) == wanted || WhatsAppBotText.Normalize(p.Code) == wanted))
                    .ToList();

                // Two lines sharing a name are a choice the bot cannot make — the same rule as
                // ResolveProductLineAsync, which returns nothing rather than row order.
                return matches.Count == 1 ? (matches[0].Id.ToString(), null) : (null, "Produit inconnu du catalogue.");
            }

            default:
                if (ctx.CustomField(code) is not { } field) return (null, "Champ non pris en charge.");

                // Checked against the form now, written as typed: the form's own normalisation runs
                // again at the write, on the value it accepted here.
                var (_, rejected) = CustomFieldValues.ApplyBestEffort(
                    ctx.LeadForm!, null, new Dictionary<string, JsonElement> { [field.Key] = JsonSerializer.SerializeToElement(value) },
                    NativeValues(lead));

                return rejected.Count == 0 ? (value, null) : (null, "Valeur refusée par le formulaire.");
        }
    }

    /// <summary>Puts a normalised value on the lead. Returns the refusal, or null when written.</summary>
    public string? Write(Lead lead, string code, string value, BotFieldContext ctx)
    {
        Sankore.Shared.Kernel.Result result;

        switch (code)
        {
            case WhatsAppBotFields.FirstName:
                result = lead.Update(null, firstName: value, lastName: null, null, null, null, null, null, null, null, null, null);
                break;

            case WhatsAppBotFields.LastName:
                result = lead.Update(null, firstName: null, lastName: value, null, null, null, null, null, null, null, null, null);
                break;

            case WhatsAppBotFields.Gender:
                if (!Enum.TryParse<LeadGender>(value, out var gender)) return "Genre illisible.";
                result = lead.Update(null, null, null, gender, null, null, null, null, null, null, null, null);
                break;

            case WhatsAppBotFields.Email:
                result = lead.SetEmail(value, emailIndexer.Compute(value));
                break;

            case WhatsAppBotFields.City:
                result = lead.EnrichPrincipalIdentity(null, null, null, null, null, null, null, city: value, null, clock.GetUtcNow());
                break;

            case WhatsAppBotFields.Neighbourhood:
                result = lead.EnrichPrincipalIdentity(null, null, null, null, null, null, null, null, null, clock.GetUtcNow(), neighbourhood: value);
                break;

            case WhatsAppBotFields.DesiredAmount:
            {
                var parts = value.Split(' ', 2);
                if (parts.Length != 2 || !decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                    return "Montant illisible.";
                result = lead.Update(null, null, null, null, null, null, desiredAmount: new Money(amount, parts[1]), null, null, null, null, null);
                break;
            }

            case WhatsAppBotFields.Product:
            {
                if (LineOf(lead, ctx.Catalogue) is not null) return "Le produit est déjà connu.";
                if (!Guid.TryParse(value, out var line)) return "Produit illisible.";

                // The lead keeps what was ASKED — the catalogue name — and no line of its own: its
                // project will carry one (product owner, 2026-10-24).
                if (ctx.Catalogue.FirstOrDefault(p => p.Id == line) is not { } product) return "Produit illisible.";
                result = lead.Update(null, null, null, null, null, interestedProduct: product.Name, null, null, null, null, null, null);
                break;
            }

            default:
            {
                if (ctx.CustomField(code) is not { } field) return "Champ non pris en charge.";

                var (json, rejected) = CustomFieldValues.ApplyBestEffort(
                    ctx.LeadForm!, lead.CustomFieldsJson,
                    new Dictionary<string, JsonElement> { [field.Key] = JsonSerializer.SerializeToElement(value) },
                    NativeValues(lead));

                if (rejected.Count > 0) return "Valeur refusée par le formulaire.";
                result = lead.SetCustomFields(json);
                break;
            }
        }

        return result.IsSuccess ? null : "La fiche ne peut plus être modifiée.";
    }

    /// <summary>
    /// The active catalogue line the lead's asked product names — code or name, accents and case
    /// ignored, a single match only — or null. What « the product is known » means now that the lead
    /// stores no line.
    /// </summary>
    public static Guid? LineOf(Lead? lead, IReadOnlyList<ProductSummary> catalogue)
    {
        if (lead is null || string.IsNullOrWhiteSpace(lead.InterestedProduct) || lead.InterestedProduct == "Unknown")
            return null;

        var wanted = WhatsAppBotText.Normalize(lead.InterestedProduct);
        var matches = catalogue
            .Where(p => p.IsActive
                     && (WhatsAppBotText.Normalize(p.Code) == wanted || WhatsAppBotText.Normalize(p.Name) == wanted))
            .Select(p => p.Id)
            .Distinct()
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Whether the field already holds a value — on the lead, or waiting on the conversation.</summary>
    /// <param name="ctx">
    /// The catalogue, to tell a known product from a free label. Without it, any asked product
    /// other than « Unknown » counts.
    /// </param>
    public static bool IsFilled(Lead? lead, IReadOnlyDictionary<string, string> pending, string code, BotFieldContext? ctx = null)
    {
        if (pending.ContainsKey(code)) return true;
        if (lead is null) return false;

        return code switch
        {
            WhatsAppBotFields.FirstName => !string.IsNullOrWhiteSpace(lead.FirstName),
            WhatsAppBotFields.LastName => !string.IsNullOrWhiteSpace(lead.LastName),
            WhatsAppBotFields.Gender => lead.Gender != LeadGender.Unknown,
            WhatsAppBotFields.Email => !string.IsNullOrWhiteSpace(lead.Email),
            WhatsAppBotFields.City => !string.IsNullOrWhiteSpace(lead.PrincipalCity),
            WhatsAppBotFields.Neighbourhood => !string.IsNullOrWhiteSpace(lead.PrincipalNeighbourhood),
            WhatsAppBotFields.Product => ctx is not null
                ? LineOf(lead, ctx.Catalogue) is not null
                : !string.IsNullOrWhiteSpace(lead.InterestedProduct) && lead.InterestedProduct != "Unknown",
            WhatsAppBotFields.DesiredAmount => lead.DesiredAmount is { Amount: > 0 },
            _ when WhatsAppBotFields.IsCustom(code) =>
                CustomFieldValues.Read(lead.CustomFieldsJson)?.ContainsKey(WhatsAppBotFields.CustomKey(code)) == true,
            _ => false,
        };
    }

    /// <summary>The product line chosen while no lead existed — what picks the profile of the next turn.</summary>
    public static Guid? PendingProduct(IReadOnlyDictionary<string, string> pending) =>
        pending.TryGetValue(WhatsAppBotFields.Product, out var id) && Guid.TryParse(id, out var line) ? line : null;

    /// <summary>
    /// A field's value for the agent's handover block — off the lead, or off the conversation while
    /// no lead exists.
    /// </summary>
    public static string? Display(Lead? lead, IReadOnlyDictionary<string, string> pending, string code, BotFieldContext? ctx)
    {
        if (lead is null)
        {
            if (!pending.TryGetValue(code, out var waiting)) return null;

            return code switch
            {
                WhatsAppBotFields.Gender => waiting == nameof(LeadGender.Male) ? "Homme" : waiting == nameof(LeadGender.Female) ? "Femme" : waiting,
                WhatsAppBotFields.Product => ctx?.Catalogue.FirstOrDefault(p => p.Id.ToString() == waiting)?.Name,
                _ => waiting,
            };
        }

        return code switch
        {
            WhatsAppBotFields.FirstName => lead.FirstName,
            WhatsAppBotFields.LastName => lead.LastName,
            WhatsAppBotFields.Gender => lead.Gender switch
            {
                LeadGender.Male => "Homme",
                LeadGender.Female => "Femme",
                LeadGender.Other => "Autre",
                _ => null,
            },
            WhatsAppBotFields.Email => lead.Email,
            WhatsAppBotFields.City => lead.PrincipalCity,
            WhatsAppBotFields.Neighbourhood => lead.PrincipalNeighbourhood,
            WhatsAppBotFields.Product => ctx is not null && LineOf(lead, ctx.Catalogue) is null ? null : lead.InterestedProduct,
            WhatsAppBotFields.DesiredAmount => lead.DesiredAmount is { } m
                ? $"{m.Amount.ToString("N0", CultureInfo.GetCultureInfo("fr-FR"))} {m.Currency}"
                : null,
            _ when WhatsAppBotFields.IsCustom(code) =>
                CustomFieldValues.Read(lead.CustomFieldsJson)?.TryGetValue(WhatsAppBotFields.CustomKey(code), out var v) == true
                    ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText())
                    : null,
            _ => null,
        };
    }

    private static Dictionary<string, string?> NativeValues(Lead? lead) =>
        new() { ["prospectType"] = (lead?.ProspectType ?? default).ToString() };
}
