namespace Sankore.Modules.Administration.PublicApi.Forms;

using Sankore.Shared.Kernel;

/// <summary>
/// The fields each entity has by construction — what its create and update contracts accept — and
/// the layout a tenant starts from.
///
/// <para>
/// <b>Code, not data.</b> A native field exists because a property and a validator exist; a tenant
/// can rename it, move it, hide it when it is optional, and choose whether the creation form shows
/// it, but cannot change its type or its options, which belong to the entity. A new native field
/// added here reaches every tenant on the next read, appended to the end of its form.
/// </para>
///
/// <para>
/// <b>Keys are the wire names</b> of the create request (<c>phoneNumber</c>, <c>preferredAgencyId</c>),
/// with a dotted path for a nested one (<c>address.city</c>), so the front can bind a field to its
/// request property without a mapping table. The option values are the enum names as serialised.
/// </para>
///
/// <para>
/// <b>Public, in the contract, for the tests of the modules that own the entities.</b> This assembly
/// cannot reference their enums, so the Leads and Customers suites pin the option lists against
/// them and check that every key here is one their create handler reports — a key renamed on one
/// side only is a required field enforced against a value nobody sends.
/// </para>
///
/// <para>
/// <b><see cref="FormField.IsRequiredLocked"/> means "the entity's own validation refuses a record
/// without it"</b>, conditions included: a company-only field required by the entity is required
/// only when visible. A tenant may make any other native field required as well; that one is then
/// enforced through <see cref="CustomFieldValues.MissingRequiredNatives"/>.
/// </para>
/// </summary>
public static class NativeFormCatalog
{
    public sealed record Catalog(IReadOnlyList<FormSection> Sections, IReadOnlyList<FormField> Fields)
    {
        public FormField? Find(string key) => Fields.FirstOrDefault(f => f.Key == key);
    }

    public static Catalog For(FormEntity entity) => entity switch
    {
        FormEntity.Lead => Lead,
        FormEntity.Client => Client,
        FormEntity.Product => Product,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null),
    };

    // ── Option lists ─────────────────────────────────────────────────────

    private static readonly FormOption[] LeadTypes =
    [
        new("Individual", "Particulier"),
        new("Corporate", "Entreprise"),
    ];

    private static readonly FormOption[] Genders =
    [
        new("Male", "Homme"),
        new("Female", "Femme"),
        new("Other", "Autre"),
    ];

    private static readonly FormOption[] LeadSources =
    [
        new("Web", "Site web"),
        new("MobileAgent", "Agent mobile"),
        new("Agency", "Agence"),
        new("CallCenter", "Centre d'appels"),
        new("Sms", "SMS"),
        new("Ussd", "USSD"),
        new("WhatsApp", "WhatsApp"),
        new("Referral", "Recommandation"),
        new("Partner", "Partenaire"),
        new("FileImport", "Import de fichier"),
        new("Campaign", "Campagne"),
    ];

    private static readonly FormOption[] LeadChannels =
    [
        new("Web", "Site web"),
        new("MobileAgent", "Agent mobile"),
        new("Agency", "Agence"),
        new("CallCenter", "Centre d'appels"),
        new("Sms", "SMS"),
        new("Ussd", "USSD"),
        new("WhatsApp", "WhatsApp"),
        new("Referral", "Recommandation"),
        new("Partner", "Partenaire"),
        new("Import", "Import"),
        new("MarketingCampaign", "Campagne marketing"),
    ];

    private static readonly FormOption[] ClientTypes =
    [
        new("Individual", "Personne physique"),
        new("Legal", "Personne morale"),
    ];

    private static readonly FormOption[] MaritalStatuses =
    [
        new("Single", "Célibataire"),
        new("Married", "Marié(e)"),
        new("Divorced", "Divorcé(e)"),
        new("Widowed", "Veuf / veuve"),
        new("FreeUnion", "Union libre"),
    ];

    private static readonly FormOption[] IdentityDocumentTypes =
    [
        new("NationalIdCard", "Carte nationale d'identité"),
        new("Passport", "Passeport"),
        new("DriverLicense", "Permis de conduire"),
        new("ConsularCard", "Carte consulaire"),
        new("VoterCard", "Carte d'électeur"),
        new("ResidencePermit", "Titre de séjour"),
        new("Other", "Autre"),
    ];

    private static readonly FormOption[] ProductCategories =
        Enum.GetValues<ProductCategory>().Select(c => new FormOption(c.ToString(), CategoryLabel(c))).ToArray();

    private static string CategoryLabel(ProductCategory category) => category switch
    {
        ProductCategory.Loan => "Crédit",
        ProductCategory.Savings => "Épargne",
        ProductCategory.Insurance => "Assurance",
        ProductCategory.HealthInsurance => "Assurance santé",
        ProductCategory.ForecastInsurance => "Assurance prévoyance",
        ProductCategory.Tontine => "Tontine",
        ProductCategory.GroupCredit => "Crédit de groupe",
        ProductCategory.Agriculture => "Agriculture",
        ProductCategory.CurrentAccount => "Compte courant",
        ProductCategory.Standard => "Produit standard",
        _ => category.ToString(),
    };

    private static FormCondition When(string key, params string[] values) => new(key, values);

    // ── Lead ─────────────────────────────────────────────────────────────

    private static readonly FormCondition Corporate = When("prospectType", "Corporate");

    private static readonly Catalog Lead = new(
        Sections:
        [
            new("identity", "Identité", 1, QuickAdd: true),
            new("company", "Entreprise", 2, QuickAdd: true, VisibleWhen: Corporate),
            new("contact", "Coordonnées", 3, QuickAdd: true),
            new("request", "Besoin", 4, QuickAdd: true),
            new("origin", "Origine", 5),
            new("assignment", "Affectation", 6),
        ],
        Fields:
        [
            Native("prospectType", FormFieldType.SingleChoice, "Type de prospect", "identity", 1, quickAdd: true,
                locked: true, options: LeadTypes, defaultValue: "Individual"),
            Native("firstName", FormFieldType.Text, "Prénom", "identity", 2, quickAdd: true, locked: true, maxLength: 150),
            Native("lastName", FormFieldType.Text, "Nom", "identity", 3, quickAdd: true, locked: true, maxLength: 150),
            Native("gender", FormFieldType.SingleChoice, "Genre", "identity", 4, options: Genders),
            Native("dateOfBirth", FormFieldType.Date, "Date de naissance", "identity", 5),
            Native("nationalId", FormFieldType.Text, "Pièce d'identité", "identity", 6, quickAdd: true, sensitive: true),

            Native("companyName", FormFieldType.Text, "Nom de l'entreprise", "company", 1, quickAdd: true),
            Native("companyEmail", FormFieldType.Email, "E-mail de l'entreprise", "company", 2),
            Native("companyPhone", FormFieldType.Phone, "Téléphone de l'entreprise", "company", 3),
            Native("website", FormFieldType.Url, "Site web", "company", 4),

            Native("phoneNumber", FormFieldType.Phone, "Téléphone", "contact", 1, quickAdd: true, locked: true, sensitive: true),
            Native("email", FormFieldType.Email, "E-mail", "contact", 2, quickAdd: true, sensitive: true),
            Native("preferredLanguage", FormFieldType.Reference, "Langue préférée", "contact", 3,
                reference: FormReferenceTarget.Language, defaultValue: "fr"),

            Native("interestedProduct", FormFieldType.Reference, "Produit souhaité", "request", 1, quickAdd: true,
                locked: true, reference: FormReferenceTarget.Product),
            Native("desiredAmount", FormFieldType.Money, "Montant souhaité", "request", 2, quickAdd: true),
            Native("comment", FormFieldType.LongText, "Commentaire", "request", 3, quickAdd: true),

            // Not locked although the contract always carries one: the capture screen never asked for
            // it and sends the default, and showing it on every creation would ask a question the
            // agent at the counter has no reason to answer.
            Native("source", FormFieldType.SingleChoice, "Source", "origin", 1,
                options: LeadSources, defaultValue: "Agency"),
            Native("leadSourceConfigId", FormFieldType.Reference, "Source configurée", "origin", 2, quickAdd: true,
                reference: FormReferenceTarget.LeadSource),
            Native("channel", FormFieldType.SingleChoice, "Canal", "origin", 3, quickAdd: true, options: LeadChannels),
            Native("campaign", FormFieldType.Text, "Campagne", "origin", 4),
            Native("externalReference", FormFieldType.Text, "Référence externe", "origin", 5),
            Native("customerReference", FormFieldType.Text, "Référence client", "origin", 6),

            // `agencyId` is the agency the lead belongs to — what the capture screen has always called
            // « Agence »; `preferredAgencyId` is the prospect's own preference, a different fact.
            Native("agencyId", FormFieldType.Reference, "Agence", "assignment", 1, quickAdd: true,
                reference: FormReferenceTarget.Agency),
            Native("preferredAgencyId", FormFieldType.Reference, "Agence préférée", "assignment", 2,
                reference: FormReferenceTarget.Agency),
            Native("ownerId", FormFieldType.Reference, "Responsable", "assignment", 3, reference: FormReferenceTarget.User),
        ]);

    // ── Client ───────────────────────────────────────────────────────────

    private static readonly FormCondition Person = When("clientType", "Individual");
    private static readonly FormCondition Legal = When("clientType", "Legal");

    /// <remarks>
    /// <c>address.*</c> binds to <c>Address</c> on an individual and to <c>HeadOfficeAddress</c> on a
    /// legal entity: one block on the screen, the same five keys, and the legal-entity create
    /// validator still requires a street or a city. <c>phoneNumbers</c> is the first number of the
    /// contract's list and <c>secondaryPhoneNumber</c> the second. It is locked because the
    /// individual contract requires one; a legal entity may give an e-mail instead, which the screen
    /// learns from its own validator, not from this flag.
    /// </remarks>
    private static readonly Catalog Client = new(
        // The defaults reproduce the client creation wizard as it stood before dynamic forms: every
        // field it asked for is on the creation form, one step per section. `clientType` is chosen
        // before the wizard opens, which is why its own section is not on the creation form.
        Sections:
        [
            new("type", "Type de client", 1, QuickAdd: false),
            new("identity", "Identité", 2, QuickAdd: true, VisibleWhen: Person),
            new("company", "Identification", 3, QuickAdd: true, VisibleWhen: Legal),
            new("document", "Pièce d'identité", 4, QuickAdd: true, VisibleWhen: Person),
            new("contact", "Coordonnées", 5, QuickAdd: true),
            new("situation", "Situation professionnelle et familiale", 6, QuickAdd: true, VisibleWhen: Person),
            new("assignment", "Affectation", 7, QuickAdd: true),
        ],
        Fields:
        [
            Native("clientType", FormFieldType.SingleChoice, "Type de client", "type", 1, quickAdd: true, locked: true,
                options: ClientTypes, defaultValue: "Individual"),

            Native("firstName", FormFieldType.Text, "Prénom", "identity", 1, quickAdd: true, locked: true, maxLength: 150, visibleWhen: Person),
            Native("lastName", FormFieldType.Text, "Nom", "identity", 2, quickAdd: true, locked: true, maxLength: 150, visibleWhen: Person),
            Native("maidenName", FormFieldType.Text, "Nom de jeune fille", "identity", 3, quickAdd: true, maxLength: 150, visibleWhen: Person),
            Native("gender", FormFieldType.SingleChoice, "Genre", "identity", 4, quickAdd: true, locked: true, options: Genders, visibleWhen: Person),
            Native("dateOfBirth", FormFieldType.Date, "Date de naissance", "identity", 5, quickAdd: true, locked: true, sensitive: true, visibleWhen: Person),
            Native("birthPlace", FormFieldType.Text, "Lieu de naissance", "identity", 6, quickAdd: true, maxLength: 150, visibleWhen: Person),
            Native("nationality", FormFieldType.Reference, "Nationalité", "identity", 7, quickAdd: true, locked: true,
                reference: FormReferenceTarget.Country, visibleWhen: Person, defaultValue: "CI"),

            Native("legalName", FormFieldType.Text, "Raison sociale", "company", 1, quickAdd: true, locked: true, maxLength: 200, visibleWhen: Legal),
            Native("legalFormCode", FormFieldType.Reference, "Forme juridique", "company", 2, quickAdd: true, locked: true,
                reference: FormReferenceTarget.LegalForm, visibleWhen: Legal),
            Native("registrationNumber", FormFieldType.Text, "N° RCCM", "company", 3, quickAdd: true, locked: true,
                maxLength: 60, sensitive: true, visibleWhen: Legal),
            Native("taxIdNumber", FormFieldType.Text, "Numéro fiscal", "company", 4, quickAdd: true, maxLength: 60, sensitive: true, visibleWhen: Legal),
            Native("incorporationDate", FormFieldType.Date, "Date de création", "company", 5, quickAdd: true, locked: true, visibleWhen: Legal),

            Native("identityDocumentType", FormFieldType.SingleChoice, "Type de pièce", "document", 1, quickAdd: true, locked: true,
                options: IdentityDocumentTypes, visibleWhen: Person),
            Native("identityDocumentNumber", FormFieldType.Text, "Numéro de pièce", "document", 2, quickAdd: true, locked: true,
                maxLength: 60, sensitive: true, visibleWhen: Person),
            Native("identityDocumentIssuedOn", FormFieldType.Date, "Délivrée le", "document", 3, quickAdd: true, visibleWhen: Person),
            Native("identityDocumentExpiresOn", FormFieldType.Date, "Expire le", "document", 4, quickAdd: true, visibleWhen: Person),

            Native("phoneNumbers", FormFieldType.Phone, "Téléphone", "contact", 1, quickAdd: true, locked: true, sensitive: true),
            Native("secondaryPhoneNumber", FormFieldType.Phone, "Téléphone secondaire", "contact", 2, quickAdd: true, sensitive: true),
            Native("email", FormFieldType.Email, "E-mail", "contact", 3, quickAdd: true, sensitive: true),
            Native("address.street", FormFieldType.Text, "Rue", "contact", 4, quickAdd: true, sensitive: true),
            Native("address.city", FormFieldType.Text, "Ville", "contact", 5, quickAdd: true, sensitive: true),
            Native("address.state", FormFieldType.Text, "Région / District", "contact", 6, quickAdd: true, sensitive: true),
            Native("address.country", FormFieldType.Reference, "Pays", "contact", 7, quickAdd: true,
                reference: FormReferenceTarget.Country, sensitive: true, defaultValue: "CI"),
            Native("address.zipCode", FormFieldType.Text, "Code postal", "contact", 8, quickAdd: true, sensitive: true),
            Native("preferredLanguage", FormFieldType.Reference, "Langue préférée", "contact", 9, quickAdd: true,
                reference: FormReferenceTarget.Language, defaultValue: "fr"),

            Native("profession", FormFieldType.Text, "Profession", "situation", 1, quickAdd: true, maxLength: 150, visibleWhen: Person),
            Native("employer", FormFieldType.Text, "Employeur", "situation", 2, quickAdd: true, maxLength: 150, visibleWhen: Person),
            Native("declaredIncome", FormFieldType.Money, "Revenu déclaré", "situation", 3, quickAdd: true, sensitive: true, visibleWhen: Person),
            Native("maritalStatus", FormFieldType.SingleChoice, "Situation familiale", "situation", 4, quickAdd: true,
                options: MaritalStatuses, visibleWhen: Person),
            Native("fatherName", FormFieldType.Text, "Nom du père", "situation", 5, quickAdd: true, maxLength: 150, visibleWhen: Person),
            Native("motherName", FormFieldType.Text, "Nom de la mère", "situation", 6, quickAdd: true, maxLength: 150, visibleWhen: Person),

            Native("agencyId", FormFieldType.Reference, "Agence de rattachement", "assignment", 1, quickAdd: true, locked: true,
                reference: FormReferenceTarget.Agency),
            Native("advisorUserId", FormFieldType.Reference, "Conseiller", "assignment", 2, reference: FormReferenceTarget.User),
        ]);

    // ── Product ──────────────────────────────────────────────────────────

    private static readonly Catalog Product = new(
        Sections:
        [
            new("general", "Informations générales", 1, QuickAdd: true),
            new("integration", "Système de gestion", 2),
        ],
        Fields:
        [
            Native("name", FormFieldType.Text, "Nom", "general", 1, quickAdd: true, locked: true, maxLength: 150),
            Native("code", FormFieldType.Text, "Code", "general", 2, quickAdd: true, locked: true, maxLength: 50,
                pattern: "^[A-Za-z0-9_-]+$"),
            Native("category", FormFieldType.SingleChoice, "Catégorie", "general", 3, quickAdd: true, locked: true,
                options: ProductCategories),
            Native("description", FormFieldType.LongText, "Description", "general", 4, quickAdd: true, maxLength: 500),
            Native("effectiveFrom", FormFieldType.Date, "En vigueur à partir du", "general", 5),

            Native("businessProductId", FormFieldType.Text, "Identifiant dans le système de gestion", "integration", 1),
            Native("businessPlatformName", FormFieldType.Text, "Système de gestion", "integration", 2),
        ]);

    private static FormField Native(
        string key,
        FormFieldType type,
        string label,
        string section,
        int order,
        bool quickAdd = false,
        bool locked = false,
        IReadOnlyList<FormOption>? options = null,
        FormReferenceTarget? reference = null,
        int? maxLength = null,
        string? pattern = null,
        string? defaultValue = null,
        bool sensitive = false,
        FormCondition? visibleWhen = null)
        => new(
            Key: key,
            Type: type,
            Label: label,
            Order: order,
            SectionKey: section,
            QuickAdd: quickAdd || locked,
            IsRequired: locked,
            Options: options,
            MaxLength: maxLength,
            Pattern: pattern,
            DefaultValue: defaultValue,
            IsSensitive: sensitive,
            VisibleWhen: visibleWhen,
            ReferenceTarget: reference,
            IsNative: true,
            IsRequiredLocked: locked);
}
