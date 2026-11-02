namespace Sankore.Modules.Leads.Features.WhatsApp.Bot;

using System.Globalization;
using System.Text.Json;
using System.Transactions;
using Microsoft.Extensions.Logging;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.FindDuplicates;
using Sankore.Shared.Kernel;
using Sankore.Shared.Kernel.ValueObject;

/// <summary>
/// What a bot turn needs to read the fields: the catalogue (the product's choices and its name)
/// and the tenant's lead form (the custom fields). The form is null when it could not be read —
/// the custom fields are then neither offered nor written, the native ones go on.
/// </summary>
internal sealed record BotFieldContext(
    IReadOnlyList<ProductSummary> Catalogue,
    EntityFormDto? LeadForm,
    string Currency = InstitutionCurrencies.Default)
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

    /// <summary>
    /// Whether the bot may ask for the field at all: a custom field the form still offers, or a
    /// native one the institution has not masked. An unreadable form masks nothing.
    /// </summary>
    public bool IsOffered(string code) =>
        WhatsAppBotFields.IsCustom(code) ? CustomField(code) is not null : !IsMaskedNative(LeadForm, code);

    public static bool IsMaskedNative(EntityFormDto? form, string code) =>
        WhatsAppBotFields.FormKeyOf(code) is { } key
        && form?.Fields.FirstOrDefault(f => f.IsNative && f.Key == key) is { IsHidden: true };
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
    ILogger<WhatsAppBotFieldValues> logger,
    IInstitutionCurrency? institutionCurrency = null)
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

        var currency = InstitutionCurrencies.Default;
        try
        {
            currency = await institutionCurrency.GetOrDefaultAsync(tenantId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("WhatsApp bot: the institution's currency could not be read ({Reason}).", ex.GetType().Name);
        }

        return new BotFieldContext(catalogue, form, currency);
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

            // Same bounds as the identity screen: a longer value would be refused at the write.
            case WhatsAppBotFields.Profession:
                return value.Length <= 150 ? (value, null) : (null, "Profession trop longue.");
            case WhatsAppBotFields.Address:
                return value.Length <= 300 ? (value, null) : (null, "Adresse trop longue.");
            case WhatsAppBotFields.PostalCode:
                return value.Length <= 20 ? (value, null) : (null, "Code postal trop long.");

            case WhatsAppBotFields.MaritalStatus:
                return MaritalStatusOf(value) is { } marital
                    ? (marital.ToString(), null)
                    : (null, "Situation attendue : célibataire, marié(e), divorcé(e), veuf / veuve ou union libre.");

            case WhatsAppBotFields.Country:
                return CountryCodeOf(value) is { } country ? (country, null) : (null, "Pays inconnu.");

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
                return WhatsAppBotEngine.ParseAmount(value, ctx.Currency) is { } amount
                    ? ($"{amount.Amount.ToString(CultureInfo.InvariantCulture)} {amount.Currency}", null)
                    : (null, $"Montant illisible, ou dans une autre monnaie que {ctx.Currency}.");

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

            case WhatsAppBotFields.Address:
                result = lead.EnrichPrincipalIdentity(null, null, null, null, null, addressLine: value, null, null, null, clock.GetUtcNow());
                break;

            case WhatsAppBotFields.PostalCode:
                result = lead.EnrichPrincipalIdentity(null, null, null, null, null, null, postalCode: value, null, null, clock.GetUtcNow());
                break;

            case WhatsAppBotFields.Country:
                result = lead.EnrichPrincipalIdentity(null, null, null, null, null, null, null, null, countryCode: value, clock.GetUtcNow());
                break;

            case WhatsAppBotFields.Profession:
                result = lead.EnrichSituation(value, null, clock.GetUtcNow());
                break;

            case WhatsAppBotFields.MaritalStatus:
                result = lead.EnrichSituation(null, value, clock.GetUtcNow());
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
            WhatsAppBotFields.Address => !string.IsNullOrWhiteSpace(lead.PrincipalAddressLine),
            WhatsAppBotFields.PostalCode => !string.IsNullOrWhiteSpace(lead.PrincipalPostalCode),
            WhatsAppBotFields.Country => !string.IsNullOrWhiteSpace(lead.PrincipalCountryCode),
            WhatsAppBotFields.Profession => !string.IsNullOrWhiteSpace(lead.Profession),
            WhatsAppBotFields.MaritalStatus => lead.MaritalStatus is not null,
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
                WhatsAppBotFields.MaritalStatus => Enum.TryParse<LeadMaritalStatus>(waiting, out var m) ? MaritalLabel(m) : waiting,
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
            WhatsAppBotFields.Address => lead.PrincipalAddressLine,
            WhatsAppBotFields.PostalCode => lead.PrincipalPostalCode,
            WhatsAppBotFields.Country => lead.PrincipalCountryCode,
            WhatsAppBotFields.Profession => lead.Profession,
            WhatsAppBotFields.MaritalStatus => lead.MaritalStatus is { } marital ? MaritalLabel(marital) : null,
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

    /// <summary>What a customer writes for each status, normalised as <see cref="WhatsAppBotText.Normalize"/> does.</summary>
    private static readonly Dictionary<string, LeadMaritalStatus> MaritalWords = new(StringComparer.Ordinal)
    {
        ["celibataire"] = LeadMaritalStatus.Single,
        ["single"] = LeadMaritalStatus.Single,
        ["marie"] = LeadMaritalStatus.Married,
        ["mariee"] = LeadMaritalStatus.Married,
        ["marie e"] = LeadMaritalStatus.Married,
        ["married"] = LeadMaritalStatus.Married,
        ["divorce"] = LeadMaritalStatus.Divorced,
        ["divorcee"] = LeadMaritalStatus.Divorced,
        ["divorce e"] = LeadMaritalStatus.Divorced,
        ["divorced"] = LeadMaritalStatus.Divorced,
        ["veuf"] = LeadMaritalStatus.Widowed,
        ["veuve"] = LeadMaritalStatus.Widowed,
        ["widowed"] = LeadMaritalStatus.Widowed,
        ["union libre"] = LeadMaritalStatus.FreeUnion,
        ["concubinage"] = LeadMaritalStatus.FreeUnion,
        ["freeunion"] = LeadMaritalStatus.FreeUnion,
    };

    internal static LeadMaritalStatus? MaritalStatusOf(string text) =>
        MaritalWords.TryGetValue(WhatsAppBotText.Normalize(text), out var status) ? status : null;

    private static string MaritalLabel(LeadMaritalStatus status) => status switch
    {
        LeadMaritalStatus.Single => "Célibataire",
        LeadMaritalStatus.Married => "Marié(e)",
        LeadMaritalStatus.Divorced => "Divorcé(e)",
        LeadMaritalStatus.Widowed => "Veuf / veuve",
        LeadMaritalStatus.FreeUnion => "Union libre",
        _ => status.ToString(),
    };

    /// <summary>
    /// The countries a customer names in French, as the screens list them, then any ISO code. A
    /// name outside this list is not guessed: the field stays to ask, or for the agent.
    /// </summary>
    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.Ordinal)
    {
        ["cote d ivoire"] = "CI",
        ["cote divoire"] = "CI",
        ["senegal"] = "SN",
        ["burkina faso"] = "BF",
        ["burkina"] = "BF",
        ["mali"] = "ML",
        ["benin"] = "BJ",
        ["togo"] = "TG",
        ["niger"] = "NE",
        ["guinee bissau"] = "GW",
        ["ghana"] = "GH",
        ["guinee"] = "GN",
        ["liberia"] = "LR",
        ["nigeria"] = "NG",
        ["cameroun"] = "CM",
        ["maroc"] = "MA",
        ["france"] = "FR",
        ["etats unis"] = "US",
    };

    internal static string? CountryCodeOf(string text)
    {
        if (CountryNames.TryGetValue(WhatsAppBotText.Normalize(text), out var code)) return code;

        var trimmed = text.Trim();
        return IsoCountry.IsKnown(trimmed) ? trimmed.ToUpperInvariant() : null;
    }

    private static Dictionary<string, string?> NativeValues(Lead? lead) =>
        new() { ["prospectType"] = (lead?.ProspectType ?? default).ToString() };
}
