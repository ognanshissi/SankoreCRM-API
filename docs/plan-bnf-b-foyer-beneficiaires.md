# BNF-B — Foyer, bénéficiaires et conformité : analyse de l'existant et plan d'exécution

> Document d'architecture et de séquencement pour les 24 stories BNF-B-01 → BNF-B-24.
> Rédigé le 2026-10-10 contre `main` (dernier commit `7477974 Remove Customer360 legacy module`).
> Le frontend est hors périmètre (document BNF-F).

---

## 1. Verdict

Le périmètre fonctionnel est net, mais **le document BNF-B décrit un socle qui existe déjà en grande
partie, sous d'autres noms**. Appliqué littéralement, il créerait des doublons structurels :

| Ce que BNF-B demande de créer | Ce qui existe déjà | Décision |
|---|---|---|
| table `integration.integration_exchanges` (BNF-B-16) | `integration.integration_commands` — payload chiffré, `PayloadFieldNames` en clair, `IdempotencyKey`, `Attempts`/`NextAttemptAt`, `ErrorFamily`, `ExternalResponseRef`, sortie batch | **réutiliser** |
| `IntegrationDiscrepancy` (BNF-B-17) | `IntegrationReconciliationRun` + `IntegrationReconciliationGap` (`GapType`, `Resolution`, `ResolvedBy`, `ResolutionNote`) | **réutiliser** |
| `IIntegrationModule` fournit l'`ExternalRef` (BNF-B-14) | `IntegrationReference` (`ConnectionId`, `EntityType`, `CrmId`, `ExternalId`, `LastSyncedAt`) | **réutiliser** |
| ports de souscription (BNF-B-16) | `ICoreBankingGateway.RequestAccountOpeningAsync`, `IInsuranceGateway.RequestPolicySubscriptionAsync` | **réutiliser** |
| `CustomerHouseholdMember` (BNF-B-13) | `ClientRelationship` — `RelationshipType {Spouse, Child, Dependent, Guarantor, Proxy, Parent, Sibling}`, `ToExternal()` avec téléphone chiffré + blind index, DOB, n° de pièce, `ValidFrom`/`ValidTo`, réciprocité | **étendre** (voir §3.2) |
| `HealthConsent` (BNF-B-09) | `LeadConsent` — `Type`, `Channel`, `Status`, `ProofReference`, `RecordedBy`, retrait tracé | **étendre** (voir §3.5) |
| `QualificationForm` (BNF-B-07) | `QualificationTemplate` — cycle Draft→Published→Archived, compteur `Version`, `Duplicate()`, portée `ProductCategory` + `ProductCode` | **étendre** |

À l'inverse, **quatre briques que BNF-B suppose acquises n'existent pas** et portent le vrai risque
de charge :

1. **Aucun chiffrement de champ dans M13.** Le module n'a que `IPhoneBlindIndexer`. Et
   `AddFieldProtection` **lève une exception au second appel** (une seule `FieldProtectionOptions`
   et un seul singleton `IFieldEncryptor` pour tout le conteneur — M01 l'a déjà consommé). Tout
   chiffrement côté leads passe donc par une registration **keyed**, sur le modèle de
   `KycFieldProtection`. C'est un prérequis de B-01, B-02, B-09 et B-11.
2. **Aucun job d'anonymisation des leads.** BNF-B-19 parle d'« étendre le job 24 mois » ; il n'y en a
   pas. Seul M01 a `IdentifyRetentionCandidatesJob` + `AnonymizeClientHandler`. B-19 est du neuf.
3. **Aucun store de paramètres tenant dans M12.** Administration n'a que `TenantNotificationSettings`,
   une table dédiée. Les paramètres FATCA/PPE du §6 n'ont pas d'endroit où atterrir.
4. **Aucun outil de test d'architecture.** Pas de `NetArchTest` ni d'`ArchUnitNET` dans
   `Directory.Packages.props`. Le test exigé par B-09 (santé jamais lue par le scoring) est du neuf.

---

## 2. Réconciliation spécification ↔ code

Écarts de nommage et de modèle à trancher **avant** la première ligne de code : chacun, pris au mot,
produit une entité parallèle ou un contrat mort.

| BNF-B | Réalité du dépôt | Conséquence |
|---|---|---|
| `Customer` | l'agrégat M01 s'appelle **`Client`** (`customers.clients`) | nommer `CustomerHouseholdMember` dans un module dont l'agrégat est `Client` installe deux vocabulaires |
| `Customer.PersonIdentity` (owned) | `Client` porte des champs **plats** : `FirstName`, `LastName`, `MaidenName`, `Gender`, `EncryptedDateOfBirth`, `DateOfBirthBlindIndex`… + `SearchKey`, `PhoneticKeyPrimary/Secondary` | un `OwnsOne` sur `Client` est une migration destructive qui traverse `SearchKeyBuilder`, `ClientMatchScorer`, l'import, l'export et l'endpoint de reveal → **voir §3.1** |
| `ProductLine` + `ProductLineId` | `ProductSpeciality` (schéma `administration`) : `Code`, `Name`, `Category` (`ProductCategory`), `ParametersJson`, cycle `IsActive`/`EffectiveFrom`/`EffectiveTo`, `BusinessProductId` | `ProductLineId` = `ProductSpeciality.Id` ; pas de nouvelle entité → **voir §3.3** |
| « en complément d'`InterestedProduct` » (B-01) | `Lead.InterestedProduct` **lie déjà le lead à un produit — mais par libellé libre**, non validé : `HasMaxLength(100).IsRequired()`, validateur `NotEmpty()` seul, valeurs réelles `"Crédit commerçant"`, `"Épargne"`, et `"Unknown"` par défaut dans `PullLeadSourceJob` et `SystemCaptureLeadEndpoint`. **Aucun appelant ne le résout** via `GetProductCategoryAsync`/`GetProductAsync` | la formule du BNF-B est juste mais sous-estime l'écart : un libellé ne peut pas porter la `BeneficiaryPolicy` → **voir §3.3** |
| `IProductCatalogModule` | n'existe pas ; `IAdministrationModule` expose déjà `GetProductAsync`, `ListProductsAsync`, `GetProductCategoryAsync` | étendre le contrat existant, ne pas en créer un second |
| `ProductCategory` = `CurrentAccount, Savings, Credit, Insurance, Other` | `Sankore.Shared.Kernel.ProductCategory` = `Loan, Savings, Insurance, HealthInsurance, ForecastInsurance, Tontine, GroupCredit, Agriculture` | l'enum du Kernel est **déjà** la liste extensible demandée ; `Credit`→`Loan`, et il manque `CurrentAccount` |
| statut lead `Assigned` (B-03) | **n'existe pas.** `LeadStatus` = `New, Open, Qualifying, Qualified, Nurturing, Recycled, Converted, Lost, Disqualified, Archived`. L'affectation est portée par `CurrentAssignmentId`/`CurrentAssignedId` | la garde de B-03 doit être « lead vivant **et** porteur d'une affectation », sur le modèle de `Lead.IsDispatchable` — pas une comparaison de statut |
| `QualificationResponse.Status`, `SubjectType`, `SubjectId`, `RespondentId`, `QuestionnaireVersion` | existant : `LeadId`, `TemplateId`, `AnsweredBy`, `AnsweredAt`, `ComputedScore`, `AnswersJson` | 5 colonnes neuves + contrainte unique ; `AnsweredBy` ≠ `RespondentId` (l'agent qui saisit vs la personne qui répond) — garder les deux |
| `ILeadsModule.GetConversionSnapshotAsync` (B-12) | `ILeadsModule` n'expose que `GetLeadAsync` / `GetLeadHistoryAsync`. La conversion actuelle est un **push** synchrone M13→M01 (`ICustomersModule.CreateFromLeadAsync`, idempotent sur `SourceLeadId`), dans un `TransactionScope.Suppress` documenté | B-12 **inverse le sens** du flux. Voir §3.4 : l'inversion n'est pas gratuite |
| `ICustomersModule.FindMatchesAsync` (B-22) | n'existe pas, mais `ClientContactPoint` porte `BlindIndex` sur téléphone/email et `Client` porte `IdentityDocumentNumberBlindIndex` → recherche par égalité, sans déchiffrement | implémentable à coût faible, **mais** : M13 et M01 n'ont pas la même clé HMAC. M13 ne peut pas calculer l'index de M01 → le contrat prend la valeur **en clair**, M01 calcule l'index |
| pipeline « Logging → TenantResolution → AgencyAuthorization → Validation → Transaction → Audit » | réel (`Program.cs:221-228`) : **Logging → Validation → AgencyAuthorization → Transaction → Audit**. `TenantResolutionMiddleware` est un middleware HTTP, pas un behavior | la validation s'exécute **avant** le contrôle de périmètre : un id d'agence hors périmètre est rejeté 400 par le validateur avant tout `AGENCY_OUT_OF_SCOPE` |
| `AllowMultipleHoldings` dans la `BeneficiaryPolicy` (B-22) | — | une règle de détention n'est pas une politique de bénéficiaires ; à ranger hors de ce bloc (§3.3) |

---

## 3. Décisions d'architecture

### 3.1 `PersonIdentity` / `PepStatus` : Kernel oui, refactoring de `Client` non

**Décision.** Créer `PersonIdentity` et `PepStatus` dans `Sankore.Shared.Kernel` comme owned types,
et les utiliser **uniquement sur les entités neuves** : `LeadHouseholdMember`, le bloc foyer côté M01,
`FatcaDeclaration`. `Lead` et `Client` **conservent leurs champs plats** ; on ajoute des projections
`Lead.PrincipalIdentity` / `Client.ToPersonIdentity()` qui lisent les colonnes existantes.

**Pourquoi.** B-02 dit « chaque module les mappe dans son propre schéma », ce qui est satisfait.
Mais convertir `Client` en `OwnsOne` toucherait `SearchKey` (maintenu par `SearchKeyBuilder`, exigé
par CLAUDE.md sur les deux côtés lecture/écriture), `PhoneticKeyPrimary/Secondary`,
`ClientMatchScorer`, l'import clients, l'export, et l'endpoint audité de reveal — pour un bénéfice de
pure forme, sur des colonnes **déjà chiffrées en production**. Le coût est une migration à risque de
perte de données ; le gain est cosmétique.

**Conséquence assumée.** Le principal n'est pas structurellement identique à un membre du foyer. Les
validateurs FluentValidation partagés exigés par B-02 s'appliquent aux deux via la projection, donc la
garantie de validation est tenue ; la garantie de *structure* ne l'est que pour les entités neuves.
Réversible plus tard, module par module.

### 3.2 Le foyer client étend `ClientRelationship` — il ne crée pas une seconde table

**Décision.** Pas de `CustomerHouseholdMember`. On étend `ClientRelationship` de
`SpouseUnionType`, `UsesPrincipalAddress`, `PepStatus`, `SourceLeadMemberId`, et du bloc
`PersonIdentity` pour les membres non-clients (il porte déjà nom, téléphone chiffré + blind index,
DOB et n° de pièce chiffré). Le « foyer » est la vue filtrée
`Type ∈ {Spouse, Child, Dependent} ∧ ValidTo IS NULL`.

**Pourquoi.** `ClientRelationship` modélise déjà le conjoint et l'enfant, en interne (`RelatedClientId`)
comme en externe (`ToExternal`), avec validité temporelle et réciprocité. Une table parallèle donnerait
deux endroits pour enregistrer un conjoint, qui divergeraient — précisément ce que l'évolution
cherche à éliminer. Le retrait logique demandé par B-13 est déjà `ValidTo` + `CloseReason`.

**Ce que ça impose.** L'invariant « au plus un conjoint actif » devient un index unique partiel sur
`client_relationships (tenant_id, client_id) WHERE type = 'Spouse' AND valid_to IS NULL`. Les lignes
existantes doivent être vérifiées avant l'ajout de l'index (migration en deux temps : détection,
puis contrainte).

Côté **lead**, en revanche, `LeadHouseholdMember` est bien une entité neuve : rien n'existe, et c'est
un enfant de l'agrégat `Lead`, pas une relation entre deux agrégats. La DDL du §3.3 du BNF-B est
reprise telle quelle.

### 3.3 `ProductLineId` = `ProductSpeciality.Id`, résolu côté serveur ; `InterestedProduct` reste le libellé capté

**Le lead est déjà lié à un produit — mais par un libellé libre.** `Lead.InterestedProduct` est un
`string` de 100 caractères, requis, validé par un seul `NotEmpty()`. Les valeurs réelles du dépôt sont
des libellés français (`"Crédit commerçant"`, `"Crédit individuel"`, `"Épargne"`), et deux chemins de
capture le remplissent à `"Unknown"` quand la source ne dit rien (`PullLeadSourceJob:221`,
`SystemCaptureLeadEndpoint:51`). Aucun appelant ne le résout contre le catalogue : le seul endroit de
M13 qui lit le catalogue, `ResolveQualificationTemplateQuery(string ProductCode)`, reçoit le code **de
son appelant**, jamais du lead. Son unique usage comportemental est
`CompatibilityScorer:34` — `agent.Specialties.Contains(lead.InterestedProduct, OrdinalIgnoreCase)`,
une comparaison de libellés.

**Décision.** Ajouter `Lead.ProductLineId` (`Guid?`, opaque, sans FK — autre schéma) **à côté** de
`InterestedProduct`, qui ne bouge pas. `ProductSpeciality` gagne une colonne `BeneficiaryPolicyJson`
(jsonb typé par un record `BeneficiaryPolicy`), `ProductCategory` gagne `CurrentAccount`, et
`IAdministrationModule` gagne `GetBeneficiaryPolicyAsync(tenantId, productLineId, ct)`.

`AllowMultipleHoldings` sort de `BeneficiaryPolicy` : c'est une règle de détention, pas de désignation.
Elle va sur `ProductSpeciality` directement, exposée dans `ProductSummary`.

**Pourquoi deux champs et non un.** Un libellé libre ne peut pas porter la politique : `HouseholdEnabled`,
`RequiredFieldsBeforeConversion`, `RequiredKycTier`, `AllowMultipleHoldings` et `IntegrationSystem` sont
tous des lectures sur une ligne de catalogue, et `"Crédit"` ou `"Unknown"` n'en désigne aucune. Migrer
`InterestedProduct` en référence de catalogue n'est pas une option de départ : il n'existe aucune table
de correspondance libellé→code pour le backfill, `"Unknown"` est irrésolvable par construction, la
colonne CSV `InterestedProduct` de l'import porte des libellés, les ingests web/webhook publics ne
connaissent pas le catalogue, et le changement casserait le `CompatibilityScorer`.

**Le précédent est dans ce dépôt, et il est exactement de cette forme** : `Lead.LeadSourceConfigId`
cohabite avec `Lead.Source` parce que l'un est un axe de reporting grossier et l'autre la configuration
qui gouverne réellement le lead. `InterestedProduct` / `ProductLineId` est le même couple : ce que le
prospect a dit, et ce qu'un agent a résolu.

**Server-set only, pour la même raison que `LeadSourceConfigId`.** `ProductLineId` est délibérément
absent de `CaptureLeadRequest` et de `SystemCaptureLeadRequest`. Rien ne validerait l'id, et un client
capable de le poser choisirait la `BeneficiaryPolicy` appliquée à son propre lead — donc les champs
exigés avant conversion, le niveau de KYC requis et l'autorisation de détentions multiples.

**Comment il se remplit.** Deux voies, dans cet ordre :
1. à la capture, résolution *best-effort* : si `InterestedProduct` correspond exactement (casse et
   accents neutralisés) au `Code` ou au `Name` d'un `ProductSpeciality` actif du tenant, l'id est posé ;
   sinon **null**, et null dit exactement cela ;
2. à l'enrichissement, l'agent choisit la ligne dans le catalogue — c'est le cas normal pour tout lead
   venu d'un formulaire public ou d'un import.

`ResolveQualificationTemplate` doit ensuite préférer `ProductLineId` au libellé quand il est résolu.

**Conséquence à assumer : la politique est nullable.** Un lead dont la ligne n'est pas résolue n'a pas
de politique, donc pas de champs exigés, donc **pas de complétude calculable** (B-04) et pas de garde
`HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` évaluable (B-03). À trancher explicitement plutôt que laisser le
cas se décider tout seul dans un handler. Position retenue : la saisie du foyer est **autorisée** sans
ligne résolue (l'agent collecte avant de qualifier la demande), la complétude renvoie
`PRODUCT_LINE_NOT_RESOLVED` comme premier élément manquant, et la conversion **refuse** avec ce même
code. Sans cela, un lead à `"Unknown"` se convertirait avec zéro champ exigé — le chemin le plus permissif
serait aussi le plus facile à atteindre.

**Cache.** ~~Il n'y a pas de Redis dans ce dépôt.~~ **Correction (vérifiée au lot 0) : Redis EST
provisionné** — `builder.AddRedis("redis")` dans `SankoreCRM.AppHost/AppHost.cs:43`,
`builder.AddRedisDistributedCache("redis")` dans `Program.cs:385`, déjà consommé par M08 pour la
résolution de provider et par M13 (`AgentCapacityService`, `AgentExclusionService`). Le cache 5 min
de B-06 est donc implémentable **tel que spécifié**, via `IDistributedCache`. Suivre le précédent
de M13 : `sp.GetService<IDistributedCache>()` et non `GetRequiredService` — il est null en test
unitaire et le code retombe sur la requête directe.

### 3.4 Conversion : garder le push, ajouter le snapshot — ne pas inverser

**Décision.** `ICustomersModule.CreateFromLeadAsync` reste le point d'entrée et reste synchrone.
On enrichit `CreateFromLeadRequest` d'un bloc `Household`, `PepStatus`, `Fatca`, `Consents`,
`QuestionnaireResponses`. `ILeadsModule.GetConversionSnapshotAsync` est ajouté **en plus**, pour le
rejeu et pour M02, mais n'est pas le chemin de la conversion.

**Pourquoi.** Le handler actuel est documenté et le raisonnement tient toujours : l'agent a besoin de
l'id client **dans sa réponse**, et un lead marqué `Converted` qui pointe vers un client inexistant est
pire qu'un appel plus lent. Le `TransactionScope.Suppress` autour de l'appel M01 existe parce que deux
`DbContext` dans une même `System.Transactions` promeuvent en transaction distribuée, que
Npgsql ne supporte pas — c'est une erreur à l'exécution, pas à la compilation. Passer par un
consommateur outbox qui rappellerait M13 remplacerait ce problème connu par une fenêtre pendant
laquelle le lead est converti et le client n'existe pas.

**Conséquence pour B-12.** Le critère « échec partiel : transaction annulée, lead laissé en Assigned »
n'est **pas** atteignable littéralement : M01 écrit sur sa propre connexion, hors de la transaction du
lead. Ce qui le remplace, et qui est déjà en place : l'idempotence sur `SourceLeadId` (index unique
filtré) — un rejeu renvoie le même client. Le lead n'est marqué converti qu'après succès de M01.
À écrire noir sur blanc dans la story avant développement.

### 3.5 Consentement santé : un `ConsentType`, pas une entité

**Décision.** Ajouter `ConsentType.HealthData`, et à `LeadConsent` les colonnes `SubjectType` /
`SubjectId` (null = le principal). Le `GivenBy` de B-09 est `RecordedBy` ; le `Proof` est
`ProofReference`.

**Pourquoi.** `LeadConsent` porte déjà statut, canal, preuve, horodatage et retrait tracé, et le
nurturing s'annule déjà sur retrait de consentement. Une entité `HealthConsent` séparée dupliquerait
cette machinerie et sortirait le consentement santé des écrans de consentement existants.

**Garde.** La saisie d'une réponse santé exige un consentement `Granted` pour ce `SubjectId` ; pour un
mineur, le `SubjectId` du répondant doit être le principal ou le conjoint (B-08 et B-09 se recoupent
ici : une seule validation, dans le handler de réponse).

### 3.6 Chiffrement : une registration keyed pour M13, trois secrets

**Décision.** Créer `Sankore.Modules.Leads/Infrastructure/Crypto/LeadsFieldProtection.cs`, copie
littérale de `KycFieldProtection` : `Key = "Leads"`, section `Leads`, clés
`Leads:FieldEncryptionKey` et `Leads:BlindIndexKey`, injection par
`[FromKeyedServices(LeadsFieldProtection.Key)]`. `HmacPhoneBlindIndexer` existant **reste** — le
blind index téléphone des leads est déjà en base et le rebaser casserait le dédoublonnage F13.2.

**Champs chiffrés côté M13** : `Lead.Email` (+ `EmailBlindIndex`), `PersonIdentity.BirthDate` et
`AddressLine` des membres du foyer, `FatcaDeclaration.UsTin`, `QualificationResponse.AnswersJson`
quand le formulaire est santé.

**Piège.** `Lead.Email` est aujourd'hui **en clair** et interrogé en SQL
(`l.Email.ToLower() == emailNorm` dans `ConvertLeadHandler`, et dans `FindDuplicates`). Le chiffrer
casse ces requêtes silencieusement : elles compileront et ne trouveront plus rien. La migration doit
(a) remplir `EmailBlindIndex` et `EmailEncrypted` par backfill, (b) **réécrire les deux comparaisons**
vers l'index, (c) supprimer la colonne en clair dans une migration ultérieure. Trois migrations, pas une.

### 3.7 Paramètres tenant : une table dédiée dans M12

**Décision.** Créer `administration.compliance_settings` — table **dédiée et typée** (une ligne par
tenant), pas un KV générique : `FatcaEnabled`, `FatcaRegime`, `FatcaGiin`, `FatcaRequiredAt`,
`FatcaForCoHolders`, `PepPostMandateMonths`. Exposée par
`IAdministrationModule.GetComplianceSettingsAsync`, écriture derrière `admin:compliance:manage`.

**Pourquoi là.** FATCA est lu par M13 (complétude), M01 (fiche client) et M02 (vigilance) : le mettre
dans un `leads_settings` ferait dépendre M01 et M02 de M13. C'est un paramètre d'**institution**, et
M12 est le module de l'institution. Le précédent du dépôt pour un paramètre M12 est
`TenantNotificationSettings` : une table dédiée, pas un KV.

**Pourquoi typée.** `FatcaGiin` a un format contraint (19 caractères `XXXXXX.XXXXX.XX.XXX`) et
l'activation est refusée sans lui. Un `Value string` + `ValueType` repousse cette règle dans un
parseur ; une colonne la laisse au validateur et à la contrainte CHECK.

**Les trois autres paramètres du §6 vont ailleurs**, chacun chez qui l'applique :
`HEALTH_RESPONSE_RETENTION_DAYS` → nouveau `leads_settings` (M13, modèle `customer_settings`) ;
`INTEGRATION_MAX_RETRIES` et `RECONCILIATION_CRON` → `IntegrationConnection`/`ConnectionSettings`,
qui existent déjà et sont par connexion, ce qui est plus juste que par tenant.

### 3.8 `ProductHolding` vit dans M01 et ne duplique pas les lectures d'Integration

**Décision.** `customers.product_holdings` est la **saisie** CRM (Draft→PendingSignature→PendingSync→
Active|Rejected→Closed). `InsuranceSubscription` (saga de souscription) et `PolicyRecord` (read model
de ce que l'assureur détient) restent dans Integration et ne bougent pas. Le lien est
`ProductHolding.ExternalRef`, alimenté au retour d'`IntegrationReference`.

Les produits préexistants (`Origin = ImportedFromBackOffice`, lecture seule) sont projetés depuis
`PolicyRecord` / `CbsSnapshot` par un consommateur M01 — **pas** par une jointure inter-schémas.

---

## 4. Plan d'exécution

Huit lots. Chaque lot est livrable et testable seul ; l'ordre est contraint par les dépendances
techniques, pas par la numérotation des stories.

### Lot 0 — Socle (prérequis, aucune story BNF-B) — ✅ LIVRÉ le 2026-10-10
*Rien dans BNF-B ne peut être développé proprement avant.*

Livré : 4 184 tests verts (dont 98 neufs), `dotnet build` 0 erreur, `--emit-openapi` 331 paths.
Deux migrations additives (`AddLeadSettings`, `AddComplianceSettings`). Détail en §9.

| # | Travail | Pourquoi d'abord |
|---|---|---|
| 0.1 | `LeadsFieldProtection` keyed + 2 secrets + valeurs dev dans `appsettings.Development.json` | B-01, B-02, B-09, B-11 chiffrent tous ; `AddFieldProtection` lève au second appel |
| 0.2 | `PersonIdentity` + `PepStatus` dans le Kernel ; validateurs FluentValidation dans **Shared.Infrastructure** | **= BNF-B-02**, consommé par tous les lots suivants. Le Kernel a **zéro dépendance tierce** par conception, FluentValidation comprise : les validateurs partagés ne peuvent pas y vivre. Les invariants qui conditionnent l'existence du type restent dans les factories et lèvent ; les règles de requête vont dans `Shared.Infrastructure/Validation/` |
| 0.3 | `administration.compliance_settings` + contrat + permission `admin:compliance:manage` | **= BNF-B-10** ; la complétude de B-04 en dépend |
| 0.4 | `leads_settings` + `ILeadSettings` + seeder par tenant (modèle `CustomerSeeder`) | B-09 (rétention santé) |
| 0.5 | `NetArchTest.Rules` dans `Directory.Packages.props` + un premier test d'architecture vert | exigé par la DoD de B-09 ; à poser avant d'avoir des données santé |

**Vérification du lot :** `dotnet build` + `--emit-openapi` vers un chemin absolu (démarre l'hôte sans
base, attrape les échecs de DI que build et test ne voient pas).

### Lot 1 — Identité du principal et rattachement client — ✅ LIVRÉ le 2026-10-10
**BNF-B-01, BNF-B-22.** Dépend de 0.1, 0.2. Détail en §10.

- `Lead` : `ProductLineId` (`Guid?` opaque, **server-set**, résolu best-effort depuis `InterestedProduct`
  qui ne bouge pas — §3.3), `CustomerId` (opaque, sans FK), `Kind` (dérivé de `CustomerId`),
  `EmailEncrypted` + `EmailBlindIndex`. `FullName` calculé pour les nouveaux leads, inchangé pour les anciens.
- `ICustomersModule.FindMatchesAsync(tenantId, phone, email, documentNumber, ct)` — **valeurs en clair**,
  M01 calcule ses propres blind index (§2). Retourne `(ClientId, MatchedOn[], IsExact)`.
- Rattachement auto si correspondance **unique et exacte** ; sinon suggestion. Rattachement/détachement
  manuel avec motif, audité (`IResourceCommand`).
- Migration email en **trois temps** (§3.6). Réécrire les comparaisons dans `ConvertLeadHandler` et
  `FindDuplicates` **dans le même lot** — sinon le dédoublonnage par email tombe en silence.
- `LeadLinkedToCustomer` via outbox, id seul.
- `ILeadsModule.GetOpenLeadsForCustomerAsync` pour la fiche client.
- Dispatching : critère « historique client » + source « client existant » dans le scoring.

**Risque principal :** la migration email. Un backfill sur une table de leads volumineuse, et deux
requêtes LINQ qui continuent de compiler après le changement de colonne.

### Lot 2 — Politique par ligne de produit — ✅ LIVRÉ le 2026-10-10
**BNF-B-06.** Dépend de 0.3. Détail en §11.

- `BeneficiaryPolicy` (record jsonb typé) sur `ProductSpeciality` ; `AllowMultipleHoldings` à côté, pas dedans.
- `ProductCategory` += `CurrentAccount`.
- Résolution `InterestedProduct` → `ProductSpeciality` (code **ou** nom, casse et accents neutralisés,
  produits actifs du tenant) + `ResolveQualificationTemplate` qui préfère la ligne résolue au libellé.
- `IAdministrationModule.GetBeneficiaryPolicyAsync` + `IMemoryCache` 5 min invalidé à l'écriture (§3.3).
- Rôles de désignation configurables par ligne.
- Audit de toute modification.

*Lot 2 est parallélisable avec le lot 1 — modules disjoints.*

### Lot 3 — Foyer du lead et PPE — ✅ LIVRÉ le 2026-10-10
**BNF-B-03, BNF-B-05.** Dépend de 0.2, lot 2. Détail en §12.

- `LeadHouseholdMember` (enfant de l'agrégat `Lead`), DDL du §3.3 BNF-B telle quelle, index unique
  partiel `uq_lead_one_spouse` inclus.
- `Lead.AddHouseholdMember()` / `RemoveHouseholdMember()` portent les invariants ; retrait logique.
- `UsesPrincipalAddress` : l'adresse est **résolue à la lecture**, jamais recopiée.
- Garde d'autorisation : **lead vivant et affecté** (`CurrentAssignmentId is not null` + statut non
  terminal), agent assigné ou superviseur via `PermissionAttribution`. **Pas** de comparaison au statut
  `Assigned`, qui n'existe pas (§2).
- Refus `HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` si la politique ne l'active pas.
- `Lead.HasPepExposure` calculé ; `PEP_POST_MANDATE_MONTHS` depuis `compliance_settings`.
- Propagation `PepLink = FamilyMember` par défaut si le principal est PPE (`Self`), modifiable avec motif.
- Permission `lead:pep:read` pour les détails PPE.
- `LeadPepExposureChanged` via outbox.

### Lot 4 — Questionnaires multi-personnes — ✅ LIVRÉ le 2026-10-10
**BNF-B-07, BNF-B-08, BNF-B-09.** Dépend de 0.1, 0.4, 0.5, lot 3. Détail en §13.

- `QualificationTemplate` += `SubjectScope`, `IsHealthQuestionnaire`, `ExcludeFromScoring`
  (forcé `true` si santé, non modifiable). Santé rattachable seulement à une catégorie d'assurance
  (`Insurance`, `HealthInsurance`, `ForecastInsurance` — l'enum du Kernel en a trois, pas une).
- `QualificationResponse` += `SubjectType`, `SubjectId`, `RespondentId`, `QuestionnaireVersion`, `Status` ;
  contrainte unique `(lead_id, template_id, subject_type, subject_id)`. Le `Version` du template est
  **figé à la création de la réponse**, pas lu à la lecture.
- Création automatique des réponses `ToComplete` à l'ajout d'un membre ; sortie du périmètre de
  complétude au retrait, sans suppression.
- Mineur : `RespondentId` obligatoire ∈ {principal, conjoint}.
- Santé : `AnswersJson` chiffré en bloc, **sans blind index** ; permission `lead:health:read` ;
  consentement obligatoire (§3.5) ; purge selon `HEALTH_RESPONSE_RETENTION_DAYS` après transmission.
- **Test d'architecture** : ni `LeadScoreCalculator` ni le dispatching ne référencent le type des
  réponses santé. *Aujourd'hui `LeadScoreCalculator` ne lit pas du tout `QualificationResponse` — il
  score sur le profil, la source et les activités. Le test épingle un état de fait ; il est bon marché
  et c'est le moment de le poser.*
- `MISSING_QUESTIONNAIRE_RESPONSES` bloque la conversion.

**Note de migration.** `ExecuteDeleteAsync` est non supporté par EF InMemory (toute la suite de tests
l'utilise) : tout chemin de mise à jour des questionnaires doit passer par
`AsTracking().ToListAsync()` + `RemoveRange`, en snapshotant les nouveaux enfants **avant** de charger
les anciens — voir `ActivateTemplateHandler` dans Workflow, qui a payé cette leçon.

### Lot 5 — FATCA — ✅ LIVRÉ le 2026-10-10
**BNF-B-11.** Dépend de 0.1, 0.3. Détail en §14.

- `FatcaDeclaration` : une active par personne, `Indicia` jsonb (les 7 indices du BNF-B),
  `UsTinEncrypted`, `SelfCertDocRef` / `EvidenceDocRef` (références `IFileStore`, pas des blobs),
  `SignedAt`, `SupersededAt`.
- Statut **calculé**, jamais saisi : `NotApplicable` / `NonUsPerson` / `IndiciaPending` / `UsPerson` /
  `IndiciaCured` / `Recalcitrant`.
- Historisation : une nouvelle déclaration supersède l'ancienne (`SupersededAt`), rien n'est supprimé.
- Extension aux co-titulaires si `FatcaForCoHolders`.
- `FatcaStatusChanged` via outbox ; `UsPerson` et `Recalcitrant` notifiés conformité (M08).
- FATCA désactivé ⇒ aucun champ exigé, exposé **ni calculé** dans la complétude.

### Lot 6 — Enrichissement, complétude et conversion
**BNF-B-04, BNF-B-12, BNF-B-23, BNF-B-24.** Dépend des lots 1 à 5.

- `EnrichLeadCommand` : identité du principal, PPE, FATCA ; rattachement optionnel à la `LeadActivity`
  qui l'a produit.
- **Calculateur de complétude** : un service unique, piloté par `BeneficiaryPolicy.RequiredFieldsBeforeConversion`
  + `compliance_settings`. Réponse API : pourcentage + manquants **par personne**, codes stables.
  Ligne de produit non résolue ⇒ `PRODUCT_LINE_NOT_RESOLVED` en premier manquant et conversion refusée (§3.3).
- `LeadEnriched` via outbox, id seul.
- **Lead `ExistingCustomer` (B-23)** : aucune copie de l'identité, du foyer, de la PPE ni de FATCA dans
  `leads`. L'API du lead lit via le contrat M01 ; toute modification passe par les commandes M01.
  Questionnaires propres à la demande, `SubjectType ∈ {CustomerPrincipal, CustomerHouseholdMember}`,
  jamais de réutilisation d'une réponse santé antérieure.
  Contrôles « à jour » : KYC valide (ni expiré ni en revue), FATCA documenté, niveau KYC requis par la ligne.
- **Conversion (B-12 / B-24)** : branchement sur `Kind`. `NewProspect` → `CreateFromLeadAsync` enrichi
  (§3.4). `ExistingCustomer` → ni client ni dossier KYC, un `ProductHolding` Draft rattaché, les réponses
  rattachées à ce holding, désignations proposées depuis le foyer. Idempotence `SourceLeadId` dans les
  deux branches.
- `LeadConverted` += `ConversionType`, `CustomerId`, `ProductLineId`.
- `KycRequestedIntegrationEvent` += exposition PPE ⇒ `KycFile.SetVigilanceLevel(High)` à la création
  (la méthode existe déjà).

**Le point dur du lot.** Le critère « échec partiel : transaction annulée » de B-12 n'est pas tenable
tel quel (§3.4). À reformuler avec le PO avant développement, sinon la story ne sera jamais « terminée ».

### Lot 7 — Foyer client, produits détenus, désignations
**BNF-B-13, BNF-B-14, BNF-B-15.** Dépend des lots 2, 6.

- Extension de `ClientRelationship` (§3.2) + index unique partiel conjoint, **en deux migrations**
  (détection des doublons existants, puis contrainte).
- Retrait d'un membre ⇒ ses désignations actives passent `ToReview`, jamais supprimées ; alerte agent.
- `Client.HasPepExposure` recalculé ; une hausse déclenche une revue KYC événementielle.
- `ProductHolding` (§3.8) : machine à états, `Specifics` jsonb typé par catégorie, lecture paginée
  filtrable par catégorie et statut. `Origin = ImportedFromBackOffice` ⇒ lecture seule.
- `BeneficiaryDesignation` : rôles et cardinalités contrôlés par la politique ; somme des quotes-parts
  = 100 % par rang quand le rôle l'exige ; même membre désignable sur plusieurs produits ;
  sur un holding `Active`, toute modification **crée une version** (avenant), l'ancienne clôturée par `ValidTo`.
- Concurrence : `xmin` (`Version`), `CONCURRENCY_CONFLICT` — convention M01 existante.

### Lot 8 — Integration, réconciliation, anonymisation, API
**BNF-B-16, BNF-B-17, BNF-B-19, BNF-B-20, BNF-B-18.** Dépend du lot 7.

- B-16 : signature obligatoire avant `PendingSync` ; `ProductSubscriptionRequested` et
  `BeneficiaryDesignationChanged` via outbox, ids seuls ; consommateur Integration qui crée un
  **`IntegrationCommand`** (table existante) avec `IdempotencyKey = HoldingId + Version` ;
  retour ⇒ `ExternalRef` + `Active`, ou `Rejected` + motif lisible. Les retries à backoff, les familles
  d'erreur et la file de rejet existent — **ne rien réécrire**.
- B-17 : étendre l'`IntegrationReconciliationRun` existant aux `ProductHolding` et aux désignations.
  Pas d'écrasement automatique ; arbitrage par `GapResolution`. `IntegrationDiscrepancyDetected` → M08.
- B-19 : **job neuf** — orchestrateur multi-tenant sous identité SYSTEM
  (`BackgroundJobContext.SetScope` **avant** la création du scope DI), anonymisation à 24 mois couvrant
  `Lead`, `LeadHouseholdMember`, `QualificationResponse`, `LeadConsent` (santé) et `FatcaDeclaration`.
  Un lead `ExistingCustomer` ⇒ **seules** les données propres au lead, jamais la fiche client.
  Test d'intégration : aucune donnée nominative résiduelle.
- B-18 : contrat de capture v2 (prénom/nom/email séparés, foyer et questionnaires optionnels, jamais de
  santé sans preuve de consentement) ; v1 `FullName` toujours acceptée et mappée ; SDK `forms.min.js`
  mis à jour — **nouvelle version pinée**, les snippets épinglent la version exacte (décision PO existante).
- B-20 : endpoints, `canEdit`/`canConvert` par réponse, périmètre agence + RBAC, puis
  `--emit-openapi` **chemin absolu** + `npm run openapi-generator` côté SankoreFront.

**Rappel qui coûte cher sinon.** Les payloads polymorphes (`Specifics`, `BeneficiaryPolicy`) ont besoin
d'un schema filter Swashbuckle : le discriminant est invisible dans le document OpenAPI et le client
généré ne peut émettre qu'un corps qui part en 422.

### BNF-B-21 — Phase 2 France
Hors lot. `FrenchSocialSecurity` est **prévu mais inactif** dans `PersonIdentity` dès 0.2 (B-02 le
demande explicitement) ; le NIR, sa clé 97, la cohérence genre/date, la Corse 2A/2B et le code organisme
ne sont développés qu'en phase 2, sous garde « pays du tenant = FR ».

---

## 5. Séquencement

```
Lot 0 ─┬─> Lot 1 ──┬────────────────> Lot 6 ──> Lot 7 ──> Lot 8
       │           │                    ▲
       ├─> Lot 2 ──┴──> Lot 3 ──> Lot 4 ┤
       │                                │
       └──────────────> Lot 5 ──────────┘
```

Lots 1 et 2 parallélisables (modules disjoints). Lot 5 (FATCA) parallélisable avec 3 et 4 dès que 0.1
et 0.3 sont livrés. Lots 6, 7, 8 strictement séquentiels.

---

## 6. Registre des risques

| # | Risque | Impact | Parade |
|---|---|---|---|
| R1 | Chiffrement de `Lead.Email` — deux requêtes LINQ continuent de compiler et ne trouvent plus rien | dédoublonnage par email mort en silence | migration en 3 temps, réécriture des comparaisons dans le même lot, test qui échoue si la colonne en clair est encore lue |
| R2 | `AddFieldProtection` au lieu d'une registration keyed pour M13 | **les données M01 déjà chiffrées deviennent indéchiffrables** | le helper partagé lève désormais ; `LeadsFieldProtection` est au lot 0, pas improvisé |
| R3 | Index unique conjoint sur `client_relationships` avec des doublons déjà en base | migration en échec en production | migration en 2 temps : requête de détection livrée et exécutée avant la contrainte |
| R4 | B-12 « transaction annulée » non atteignable (deux `DbContext`, pas de transaction distribuée Npgsql) | story jamais « terminée » | reformuler avec le PO **avant** le lot 6 ; l'idempotence `SourceLeadId` est la garantie réelle |
| R5 | `ExecuteDeleteAsync` non supporté par EF InMemory | chemins de mise à jour des questionnaires non testés, découverts en prod | `AsTracking` + `RemoveRange`, snapshot des nouveaux enfants avant chargement des anciens |
| R6 | Payloads polymorphes absents du document OpenAPI | le client Angular généré émet des corps qui partent en 422 | schema filter Swashbuckle livré avec le lot qui introduit le jsonb typé |
| R7 | ~~Pas de Redis~~ — **écarté au lot 0** : Redis est provisionné et déjà utilisé par M08 et M13 | — | B-06 s'implémente via `IDistributedCache` ; résoudre avec `GetService`, null en test unitaire |
| R10 | `ProductLineId` nullable : un lead `"Unknown"` sans politique n'a aucun champ exigé | le chemin le plus permissif devient le plus facile à atteindre | complétude et conversion refusent `PRODUCT_LINE_NOT_RESOLVED` ; test qui épingle le refus |
| R11 | `ProductLineId` exposé dans un contrat de capture public | un appelant choisit la politique qui gouverne son propre lead (champs exigés, niveau KYC, détentions multiples) | server-set only, absent de `CaptureLeadRequest`/`SystemCaptureLeadRequest`, comme `LeadSourceConfigId` |
| R8 | 24 stories, ~150 SP, 6 modules touchés | dérive de périmètre | les lots 0 à 4 livrent une valeur utilisable seule (foyer + PPE + questionnaires) sans Integration |
| R9 | Front invisible jusqu'à régénération du client | endpoint livré et inutilisable | `--emit-openapi` **chemin absolu** + `npm run openapi-generator` dans la DoD de chaque lot exposant un endpoint |

---

## 7. Définition de terminé, par lot

Reprise de la DoD du §8 BNF-B, rendue vérifiable :

- [ ] critères d'acceptation de chaque story du lot
- [ ] tests unitaires > 80 % du code de la story (xUnit + FluentAssertions + NSubstitute, EF InMemory)
- [ ] tests d'intégration avec le bouchon par adaptateur — `Sankore.Modules.Integration.Adapters.Fake` existe, l'utiliser
- [ ] isolation multi-tenant testée (un tenant ne voit rien de l'autre, filtres globaux en place)
- [ ] chiffrement vérifié : la valeur n'est **jamais** en colonne claire, l'audit ne trace que des **noms** de champs
- [ ] exclusion des données de santé du scoring couverte par le test d'architecture
- [ ] migration EF Core relue — `--context` explicite, non destructive, nouveaux champs nullables
- [ ] `dotnet run --project src/Bootstrapper/Sankore.Api -- --emit-openapi <chemin ABSOLU>` passe
- [ ] client front régénéré si le lot expose un endpoint

---

## 8. Décisions à confirmer avant le lot 0

Quatre points où ce plan s'écarte sciemment de la lettre du BNF-B. Chacun est réversible, mais le
choix doit être su :

1. **`PersonIdentity` n'est pas rétro-appliqué à `Client` ni à `Lead`** (§3.1) — refactoring de
   colonnes PII déjà chiffrées contre un bénéfice de forme.
2. **Le foyer client étend `ClientRelationship`**, il n'y a pas de `CustomerHouseholdMember` (§3.2) —
   sinon deux endroits pour enregistrer un conjoint.
3. **`ProductLineId` est l'id d'un `ProductSpeciality`**, posé par le serveur, et `InterestedProduct`
   reste le libellé capté (§3.3) — deux champs, sur le modèle `Source`/`LeadSourceConfigId`. À valider
   contre ce que BNF-F appelle « ligne de produit » : si le front attend un niveau **au-dessus** du
   produit, cette décision tombe.
4. **La conversion reste un push M13→M01** ; `GetConversionSnapshotAsync` est ajouté en complément,
   pas en remplacement (§3.4) — et le critère « transaction annulée » de B-12 doit être reformulé.

---

## 9. Lot 0 — ce qui a été livré (2026-10-10)

### 0.1 — Chiffrement de champ pour M13, keyed
- `src/Modules/Leads/.../Infrastructure/Crypto/LeadsFieldProtection.cs` — `Key = "Leads"`, section
  `Leads`, enregistrement **keyed** (`AddKeyedSingleton<IFieldEncryptor>`), calqué sur
  `KycFieldProtection`. Appelé depuis `LeadsModule.AddLeadsModule`.
- `IPhoneBlindIndexer` / `HmacPhoneBlindIndexer` **conservés** : leurs index sont déjà en base et
  F13.2 les lit.
- Clés dev dans `appsettings.Development.json` (section `Leads`, 32 octets base64 chacune).
  Injection : `[FromKeyedServices(LeadsFieldProtection.Key)] IFieldEncryptor`.

### 0.2 — Value objects et mapping partagés
| Fichier | Rôle |
|---|---|
| `Kernel/ValueObject/PersonIdentity.cs` | identité d'une personne physique ; `WithAddressOf` (héritage d'adresse résolu **à la lecture**), `AgeAt`, `Anonymized` |
| `Kernel/ValueObject/PepStatus.cs` | statut PPE ; **`IsExposedAt(asOf, postMandateMonths)` est la définition unique de la fenêtre post-mandat** (B-05), `AsFamilyMemberLink` pour la propagation au foyer |
| `Kernel/ValueObject/PepLink.cs` | `Self` / `FamilyMember` / `CloseAssociate` |
| `Kernel/ValueObject/PersonGender.cs` | `Unknown` en premier, donc `default` ; nommé `PersonGender` car M01 occupe déjà `Gender` |
| `Kernel/ValueObject/IsoCountry.cs` | codes ISO 3166-1 alpha-2 dérivés de l'ICU du runtime, pas d'une table ; `HasValidShape` (value objects) vs `IsKnown` (validateurs) |
| `Kernel/ValueObject/FrenchSocialSecurity.cs` | B-21 phase 2, **déclaré et inactif**, sans validation |
| `Shared.Infrastructure/Validation/PersonIdentityValidator.cs` | formats, dates cohérentes, pays ISO réel, refus du bloc français |
| `Shared.Infrastructure/Validation/PepStatusValidator.cs` | fonction obligatoire si PPE, pas de détail si non-PPE, date de fin non future |
| `Shared.Infrastructure/Persistence/PersonMapping.cs` | `OwnsPersonIdentity` / `OwnsPepStatus` — **une** liste de colonnes pour tous les modules, chacun passant **son** encryptor |
| `Shared.Infrastructure/Persistence/EncryptedConverters.cs` | `Text` et `Date` : le chiffrement passe dans le mapping, un handler ne peut plus l'oublier |

Deux décisions à connaître avant d'écrire le lot 3 :
- **Les noms sont requis, tout le reste est optionnel.** Un owned type dont toutes les colonnes sont
  nulles est indiscernable d'un propriétaire absent pour EF, et un membre du foyer sans nom n'est
  pas une ligne exploitable. Un appelant qui ne connaît qu'une partie de la personne porte un
  `PersonIdentity?` et le laisse null.
- **`Date` sérialise en `yyyy-MM-dd` + `InvariantCulture`, des deux côtés.** Chiffré, un aller-retour
  par la culture du process est silencieux : la valeur se déchiffre parfaitement et c'est le mauvais
  jour. Deux tests épinglent l'écriture fr-FR → lecture invariante et l'inverse.

### 0.3 — `administration.compliance_settings` (= BNF-B-10, terminé)
- `ComplianceSettings` (table dédiée typée, une ligne par tenant, `xmin`) + `FatcaRegime` /
  `FatcaRequiredAt`.
- `ConfigureFatca` est **tout ou rien** : activer sans régime ni GIIN bien formé est refusé
  (`FATCA_REGIME_REQUIRED` / `FATCA_GIIN_REQUIRED` / `FATCA_GIIN_INVALID`) et ne modifie **rien**,
  pas même les champs annexes. Désactiver **efface** régime et GIIN.
- `SetPepPostMandateMonths` : 0..600. Zéro est légitime (« exposé seulement en fonction »), le
  plafond empêche une coquille de rendre tout ancien élu exposé à vie.
- `GET` / `PUT api/v1/compliance-settings`, permissions `admin:compliance:read` et
  `admin:compliance:manage` (ajoutées à `Permissions.All`, donc seedées au démarrage).
- `IAdministrationModule.GetComplianceSettingsAsync` → `ComplianceSettingsDto`, **jamais null** :
  un tenant sans ligne tourne sur les défauts, et répondre null ferait trancher « pas de ligne, donc
  FATCA actif ? » à chaque appelant.
- Migration `AddComplianceSettings`.

### 0.4 — `leads.lead_settings` (= socle de BNF-B-09)
- `LeadSetting`, `LeadSettingKeys`, `ILeadSettings`, `LeadSettingsService`, `LeadSettingSeeder`
  (appelé depuis `LeadsModule.InitializeAsync`), migration `AddLeadSettings`.
- Une clé pour l'instant : `health-response-retention-days` = 90. Les suivantes arrivent au lot 4.
- Copie assumée de `CustomerSettingsService`, cache statique 60 s inclus. **Piège de test** : ce
  cache est `static` et indexé par `(tenantId, key)`, donc deux tests partageant un id de tenant
  voient les écritures l'un de l'autre. Un id **par test** (champ d'instance, xUnit construit une
  instance par test) — la même règle vaut pour tout test écrit contre `CustomerSettingsService`.

### 0.5 — Tests d'architecture
- `NetArchTest.Rules` 1.3.2 dans `Directory.Packages.props`, référencé par `Leads.Tests`.
- `Architecture/ScoringIsolationTests.cs` : `LeadScoreCalculator` et tout le namespace
  `Features.DispatchLead` ne dépendent pas de `QualificationResponse`.
- Un troisième cas garde les deux premiers : **NetArchTest répond « succès » sur une sélection
  vide**, donc une classe renommée ou un namespace déplacé rendrait les règles vertes en ne
  vérifiant rien. Le cas assure que les sélecteurs matchent encore quelque chose.
- **Ce que le fichier ne vérifie pas encore**, et que le lot 4 doit ajouter : que le type exposant
  les réponses santé **déchiffrées** n'est référencé que depuis sa propre tranche. Tant que ce type
  n'existe pas il n'y a rien à nommer, et une règle portant sur `QualificationResponse` entière ne
  peut pas en tenir lieu — les réponses non-santé alimentent légitimement le score via
  `QualifyLeadHandler`.

### Vérifications passées
```
dotnet build                     → 0 erreur
tous les projets de test         → 4 184 tests, 0 échec (98 neufs)
--emit-openapi <chemin absolu>   → 331 paths, GET + PUT /api/v1/compliance-settings présents,
                                   schémas UpdateComplianceSettingsCommand et
                                   ComplianceSettingsResponse émis
```

### Reste à faire avant d'enchaîner
1. `dotnet ef database update` des deux contextes sur chaque environnement.
2. Poser `Leads:FieldEncryptionKey` et `Leads:BlindIndexKey` hors Development (user-secrets /
   variables d'environnement). Les clés sont validées **à la première utilisation**, pas au boot :
   un oubli ne casse pas le démarrage, il casse la première écriture chiffrée du lot 1.
3. Régénérer le client front (`--emit-openapi` chemin ABSOLU, puis `npm run openapi-generator`) —
   les deux endpoints de compliance sont invisibles côté Angular sans cela.
4. Trancher les quatre décisions du §8 avant le lot 1.

---

## 10. Lot 1 — ce qui a été livré (2026-10-10)

**BNF-B-01 ✅ et BNF-B-22 ✅.** 4 251 tests verts (67 neufs), `dotnet build` 0 erreur,
`--emit-openapi` 334 paths, une migration additive `AddLeadCustomerLinkAndEmailProtection`.

### Protection de l'e-mail — le point dur, livré en trois colonnes

| Colonne | Propriété CLR | Rôle |
|---|---|---|
| `email_enc` | `Lead.Email` (clair) | AES-256-GCM via un **ValueConverter EF**, clé M13 |
| `email_blind_index` | `Lead.EmailBlindIndex` | HMAC-SHA256, indexé `(tenant_id, …)` — la moitié cherchable |
| `email` | `Lead.LegacyEmailClear` | **transitoire**, lecture seule, pour les leads d'avant la migration |

Le chiffrement est dans le **mapping**, pas dans les handlers : un handler ne peut plus l'oublier,
et toutes les projections de lecture existantes (`ListLeads`, `GetPipeline`, `ExportLeads`,
`GetLead`, `ExecuteNurturingJob`) continuent de fonctionner sans changement.

**Les trois sites de comparaison SQL ont été réécrits** vers le blind index —
`CaptureLeadHandler`, `ConvertLeadHandler`, `FindDuplicatesHandler` — chacun avec la branche de
repli transitoire sur `LegacyEmailClear`, calquée sur le repli téléphone qui existait déjà.
`IdentityMatchScorer` lit `Email ?? LegacyEmailClear` : sans cela un candidat trouvé par le repli
n'aurait marqué aucun point e-mail et aurait pu tomber sous le seuil de confiance.

**`LeadEmailProtectionBackfill`** tourne dans `LeadsModule.InitializeAsync`, **avant que l'hôte ne
serve sa première requête** : par lots de 500, idempotent, reprenable, et il journalise en
*warning* le nombre de lignes restantes — `leads.leads.email` ne peut pas être supprimée tant que
ce nombre n'est pas zéro.

**`Lead.Update` n'a plus de paramètre `email`.** Il était devenu silencieusement ignoré ; le
supprimer fait pointer le compilateur sur chaque appelant. La paire passe par `SetEmail(email,
blindIndex)`, seule porte, et `ApplyMergeOverrides` prend désormais les deux — une fusion qui aurait
pris l'adresse de la source en laissant l'index de la cible aurait fait correspondre le lead fusionné
à la mauvaise adresse dans tous les dédoublonnages suivants.

### `ProductLineId` et `CustomerId`
- `Lead.ProductLineId` : `Guid?` opaque, **server-set**, absent des contrats d'ingest publics.
  `Lead.SetProductLine` traite `Guid.Empty` comme absent (c'est ce que produit le `GuidConverter`
  de ce projet sur un guid JSON malformé). La **résolution** depuis `InterestedProduct` reste au lot 2.
- `Lead.CustomerId` + `CustomerLinkReason` + `CustomerLinkIsAutomatic` + `CustomerLinkedAt`.
  `Lead.Kind` est **dérivé, sans colonne** (`builder.Ignore`) : une copie stockée serait une seconde
  source de vérité pour un fait dont dépend le branchement de la conversion.
- `FullName` est **dérivé** quand les deux noms structurés existent, en lecture seule à partir de là ;
  un nom complet seul est conservé tel quel et **jamais découpé**. Une seule règle,
  `Lead.ResolveFullName` — le handler ne compose plus.

### Rattachement à un client existant
- `ICustomersModule.FindMatchesAsync(tenantId, phone, email, documentNumber, ct)` — **valeurs en
  clair**, M01 calcule ses propres index : M13 et M01 n'ont pas la même clé HMAC, donc aucun
  appelant ne peut produire un index que M01 saurait comparer.
- **`CustomerMatchPolicy` est la définition unique** du rattachement automatique, partagée par le
  handler de capture et l'endpoint de suggestion : correspondance **unique** *et* portant sur un
  identifiant qui identifie une **personne** (pièce d'identité, e-mail). Un **téléphone seul ne
  rattache jamais** — un combiné familial ou une ligne de boutique est une situation ordinaire ici.
- `PUT leads/{id}/customer`, `POST leads/{id}/customer/detach`, `GET leads/{id}/customer-matches`.
  Permission `lead:update` / `lead:read`, pas de permission neuve : une nouvelle aurait renvoyé 401
  à tous les rôles jusqu'à un reseed et une reconnexion, ce que les autres commandes du module
  n'exigent de personne.
- `LeadLinkedToCustomerIntegrationEvent` / `LeadDetachedFromCustomerIntegrationEvent` via l'outbox,
  **identifiants seuls** — l'identité, le foyer, la PPE et FATCA restent sur la fiche client (B-23).
- `ILeadsModule.GetOpenLeadsForCustomerAsync` : « ouvert » ≠ « dispatchable » — un lead en
  *Nurturing* n'est pas dispatchable et **est** une demande en cours que la fiche client doit montrer.

### Scoring et dispatching
- `LeadScoreCalculator` : un lead rattaché prend **20/20** en qualité de source, plafonné au maximum
  de la dimension. Les plafonds mesurés documentés (60 walk-in, 45 import) décrivent un lead **non
  rattaché** et sont inchangés ; un import rattaché passe de 45 à 60, ce qui est l'objectif.
- `ClientAdvisorResolver` : le critère « historique client » de F13.10. Inséré **après** la liste
  d'exclusion (la décision d'un administrateur lie toujours) et **avant** les heuristiques de charge
  (`MaxTasksPerAgent`, anti-monopole), délibérément — celles-ci existent pour répartir le travail,
  et « le conseiller de ce client » n'est pas une répartition. Préférence et non exigence : un
  conseiller indisponible laisse tourner la stratégie configurée.

### Trois pièges trouvés en construisant

1. **`--emit-openapi` a attrapé un échec de démarrage que build et tests ont laissé passer.**
   `MapDelete` avec un corps : *« Body was inferred but the method does not allow inferred body
   parameters »* — les minimal APIs ne déduisent pas de corps sur DELETE. D'où
   `POST …/customer/detach`, qui évite aussi un DELETE dont certains proxys suppriment le corps.
2. **EF InMemory *déchiffre puis compare en mémoire*.** Une requête `l.Email == "x"` **trouve** la
   ligne en test et n'en trouverait **aucune** sur PostgreSQL, où le littéral est chiffré avec un
   nonce neuf avant la comparaison. **La panne la plus dangereuse de ce lot est donc invisible pour
   la suite de tests.** `LeadEmailProtectionTests` épingle l'écart explicitement, avec le nom du
   test comme avertissement — la règle « passer par le blind index » est une convention tenue par
   la relecture, pas par un test.
3. **`Leads:BlindIndexKey` était déjà dans les user-secrets** et surcharge la valeur
   `appsettings.Development.json` ajoutée au lot 0 — donc les index téléphone existants ne sont pas
   invalidés. Mais `HmacPhoneBlindIndexer` et le `HmacBlindIndexer` partagé lisent **la même clé** :
   aucune collision (le partagé préfixe par le purpose) mais un **couplage de rotation** — changer
   cette clé invalide l'index téléphone *et* l'index e-mail, et les deux demandent un backfill.

### Reste à faire avant le lot 2
1. `dotnet ef database update --context LeadsDbContext`, puis **vérifier le log du backfill** :
   `Lead e-mail protection backfill: N lead(s) encrypted and indexed`.
2. Ne **pas** supprimer `leads.leads.email` tant que le backfill n'annonce pas zéro restant. La
   suppression de la colonne, de `LegacyEmailClear` et des branches de repli est un lot séparé.
3. Régénérer le client front : 3 endpoints neufs, invisibles côté Angular sans cela.

---

## 11. Lot 2 — ce qui a été livré (2026-10-10)

**BNF-B-06 ✅.** 4 127 tests verts (52 neufs), `dotnet build` 0 erreur, `--emit-openapi` 335 paths,
une migration additive `AddBeneficiaryPolicyToProductLine`.

### Ce qui a été étendu, pas créé
Pas de nouvelle entité `ProductLine` : `ProductSpeciality` **est** le catalogue, et
`ProductLineId` **est** son `Id`. Pas de nouveau `IProductCatalogModule` non plus :
`IAdministrationModule` gagne deux méthodes. `ProductCategory` gagne `CurrentAccount` — les quatre
autres valeurs de la liste du BNF-B existaient déjà (`Credit` sous le nom `Loan`).

**Pas de valeur `Other`**, délibérément : la catégorie est ce qui résout la politique, le niveau de
KYC requis et le système d'intégration d'un produit. Un produit classé « Other » n'a aucune de ces
réponses, et la panne se verrait comme une conversion refusée pour un motif inexploitable. Un
produit qui ne rentre dans aucune valeur demande une valeur de plus — une ligne et un déploiement
sans migration.

### Le partage jsonb / colonne
| Où | Quoi | Pourquoi |
|---|---|---|
| `beneficiary_policy` (jsonb typé) | `HouseholdEnabled`, `AllowedRoles`, `MaxChildren`, `ChildMaxAge`, `RequiredFieldsBeforeConversion`, `RequiredKycTier`, `IntegrationSystem` | huit champs optionnels lus **en bloc** par M13 et M01, jamais filtrés |
| `allow_multiple_holdings` (colonne) | `AllowMultipleHoldings` | **interrogé** par l'avertissement de doublon de B-22, et ce n'est pas une règle de désignation |

`AllowMultipleHoldings` est **à `true` par défaut** : une épargne, une tontine ou un crédit se
détiennent légitimement plusieurs fois ; c'est l'exception — une police santé par personne — qu'un
administrateur déclare. Par défaut à `false`, chaque ligne aurait refusé toute seconde souscription
ordinaire jusqu'à ce que quelqu'un s'en aperçoive.

**Les rôles de désignation portent leur cardinalité et leur règle de quote-part**
(`DesignationRoleDefinition`: `Code`, `Label`, `MinCount`, `MaxCount`, `SharesMustTotal100`). Ce
n'est pas de l'anticipation : B-15 exige « somme des quotes-parts d'un même rang égale à 100 %
**quand le rôle l'exige** », donc le drapeau appartient au rôle. Les rôles sont des **chaînes par
ligne** et non une énumération de plateforme — « configurables par ligne » exclut un déploiement à
chaque fois qu'une institution invente un rôle.

### La migration a dû être refaite
EF génère `DEFAULT ''` pour une propriété convertie non-nullable. **PostgreSQL refuse `''` sur une
colonne `jsonb`** (`invalid input syntax for type json`) : la migration échoue, elle ne corrompt
rien. Corrigé par `HasDefaultValueSql("'{}'::jsonb")` — `{}` se désérialise en
`BeneficiaryPolicy.None`, puisque tous les membres du record sont optionnels, ce qui est exactement
ce qu'une ligne non configurée doit porter.

### Le cache
`ProductCatalogCache` — 5 min, `IDistributedCache` **optionnel** (`GetService`, pas
`GetRequiredService` : absent en test unitaire, et Redis peut être indisponible). Deux clés : la
politique **par ligne** (lue par lead) et le catalogue actif **par tenant** (lu par résolution de
libellé). **Un service et non du code dans le facade** : le facade lit, les cinq handlers d'écriture
invalident — cinq sites pour un format de clé, et un cache dont le lecteur et l'écrivain ne
s'accordent pas sur la clé est un cache qui **n'invalide jamais**, dont le seul symptôme est un délai
de cinq minutes qui ressemble à de l'instabilité.

L'invalidation est **après** `SaveChanges` : invalider avant ouvre une fenêtre pendant laquelle une
lecture concurrente repeuple le cache depuis la ligne d'avant l'écriture — le seul ordre qui rend un
cache définitivement faux au lieu de brièvement périmé.

### Résolution du libellé (le pont avec `InterestedProduct`)
`ResolveProductLineAsync(tenantId, label)` — **exact** sur le code **ou** le nom, casse et accents
neutralisés, produits **actifs** seulement. Jamais flou : la ligne résolue décide des champs qui
bloquent la conversion.
- **Code d'abord, puis nom.** Un code est un identifiant choisi par un administrateur ; un nom est
  un libellé que deux lignes peuvent partager — et quand elles le partagent, la résolution rend
  **null** plutôt que l'ordre des lignes.
- **`"Unknown"` ne résout rien.** C'est la valeur littérale que `PullLeadSourceJob` et
  `SystemCaptureLeadEndpoint` écrivent quand une source ne nomme aucun produit : ce n'est pas un
  libellé, c'est l'absence de libellé. Un tenant ayant un produit réellement nommé « Unknown »
  aurait sinon capté sur sa politique tous les leads sans libellé du déploiement.
- Comparaison **en mémoire** sur le catalogue chargé, pas en SQL : l'insensibilité aux accents
  demanderait `unaccent` et un index fonctionnel sur deux colonnes. Un catalogue de tenant fait des
  dizaines de lignes, et le cache ramène le coût à une requête par tenant par cinq minutes — ce qui
  compte, parce que la capture appelle ceci à chaque lead.

`ResolveQualificationTemplate` **préfère désormais la ligne résolue au libellé** (paramètre
`productLineId` optionnel) : une lecture en cache donne le code **autoritatif** et la catégorie, ce
qui économise aussi l'appel `GetProductCategoryAsync` du niveau 2 — et évite le cas où un libellé
épelle un code absent du catalogue, les niveaux 1 et 2 manquent tous les deux, et chaque lead reçoit
silencieusement le questionnaire générique. Un id pendant retombe sur le libellé plutôt que
d'échouer : la référence est opaque par construction.

### Reste à faire avant le lot 3
1. `dotnet ef database update --context AdministrationDbContext`.
2. Régénérer le client front (`PUT products/{id}/beneficiary-policy`, `ProductDto` enrichi, le
   paramètre `productLineId` de `resolve`).
3. Configurer la politique des lignes existantes : **toutes portent `{}`**, donc foyer désactivé et
   aucun rôle — ce qui est le défaut voulu, mais signifie que le lot 3 refusera la saisie du foyer
   avec `HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` sur toute ligne non configurée.

---

## 12. Lot 3 — ce qui a été livré (2026-10-10)

**BNF-B-03 ✅ et BNF-B-05 ✅.** 4 188 tests verts (61 neufs), `dotnet build` 0 erreur,
`--emit-openapi` 341 paths, une migration additive `AddLeadHouseholdAndPep`.

### Le foyer est un enfant de l'agrégat, pas une table à côté
`LeadHouseholdMember` est atteint **uniquement par `Lead`** (`Include(l => l.HouseholdMembers)`),
parce que tous les invariants portent sur **l'ensemble** : au plus un conjoint actif, le plafond
d'enfants, l'âge maximum. Une entité autonome aurait laissé chacun d'eux au handler qui écrit, et
l'index unique aurait été le seul à vraiment tenir le premier — trop tard et avec un message
inexploitable.

`HouseholdWriteContext.LoadAsync` fait `Include` + `AsTracking` : **obligatoire, pas une
optimisation**. Un lead chargé sans son foyer voit un ensemble VIDE et ajoute volontiers un second
conjoint, attrapé seulement par `uq_lead_one_spouse` sous la forme d'une erreur SQL.

La migration reprend la DDL du §3.3 : index unique partiel `uq_lead_one_spouse`, contrainte
`ck_lhm_union`, colonnes `birth_date_enc` / `address_enc` chiffrées par le mapping partagé
`PersonMapping`.

### La garde d'autorisation, et pourquoi elle ne cite pas le statut `Assigned`
**`LeadStatus.Assigned` n'existe pas** et n'a jamais existé dans ce module — l'affectation est
`CurrentAssignmentId`. `Lead.IsHouseholdEditable` exprime donc ce que le critère veut dire : le lead
n'est pas terminal **et** quelqu'un le travaille. `OwnerId` compte autant qu'une affectation : un
lead saisi au guichet avec un propriétaire nommé et jamais passé par le moteur a bien un
responsable, et lui refuser la saisie rendrait le parcours guichet inutilisable.

`LeadAccessGuard` = l'agent assigné, le propriétaire, celui qui a collecté le lead, ou **le
superviseur** de son porteur via `GetTeamAgentIdsAsync`. Trois rôles contournent sans lecture
d'équipe — `System`, `Administrator`, `SalesManager` — parce qu'un manager dont l'équipe revient
vide (tenant mono-agence) serait sinon exclu de tous les leads par un contrat qui répond « personne
ne vous est rattaché ». **`BranchManager` ne contourne PAS** : sa portée est son agence, ce que
`GetTeamAgentIdsAsync` calcule exactement.

### Les trois asymétries de panne, assumées
| Appel | Sur panne M12 | Pourquoi |
|---|---|---|
| résolution client / ligne produit **à la capture** | best-effort, on avale | un prospect au téléphone ne doit jamais être perdu |
| `LeadAccessGuard` | **échoue FERMÉ** | décide si on peut éditer le lead d'autrui ; une panne n'est pas une raison d'élargir |
| `HouseholdPolicyResolver`, fenêtre PPE | **échoue la requête** | laisser passer contournerait silencieusement le plafond d'enfants et la fenêtre LBC/FT qu'une institution a configurés ; l'agent réessaie |

Une **ligne de produit non résolue est AUTORISÉE**, pas refusée (position du §3.3) : l'agent
collecte puis qualifie. Un **id pendant** l'est aussi — la référence est opaque par construction, et
refuser bloquerait un lead dont quelqu'un d'autre a supprimé le produit.

### L'adresse héritée est un lien, et c'est vérifié à l'écriture
`UsesPrincipalAddress` vide les champs postaux du membre **au moment de l'écriture**
(`PersonIdentity.WithoutPostalAddress`), pas seulement à la lecture : une adresse périmée laissée
dans la ligne est une adresse qu'un futur « arrête d'hériter » ferait réapparaître, silencieusement
et vieille de plusieurs années. `ResolveIdentity(principal)` est le seul chemin de lecture — une
projection qui lit `Identity` directement montre un enfant sans adresse, ce qui ressemble à une
donnée manquante et serait signalé comme incomplet.

### PPE
`Lead.HasPepExposure` est **une colonne**, écrite par le seul `RecalculateHasPepExposure` : le
dispatching filtre dessus en SQL, et l'événement doit partir **au moment exact** où la valeur
change, ce qu'une propriété calculée ne peut pas remarquer. La méthode est publique pour que le job
nocturne puisse l'appeler — **une exposition peut cesser sans que personne touche au lead**, quand
la fenêtre post-mandat se ferme.

Propagation (B-05) : un principal exposé en `Self` pose `FamilyMember` sur le foyer actif, **sauf**
sur un membre portant un `OverrideReason` — celui-là a été classé par un agent, et l'écraser
jetterait un jugement que la story accorde explicitement. Un principal exposé en `FamilyMember` ne
propage rien : il n'est pas le PPE.

La **détail** PPE (fonction, pays, lien) est derrière `lead:pep:read`, et la décision est prise
**dans l'endpoint**, là où un relecteur d'autorisation regarde — pas dans le handler. Le **drapeau**
reste visible : le dispatching doit router un lead exposé sans montrer à personne pourquoi.

### Deux pannes trouvées en construisant, toutes deux par `--emit-openapi`

1. **`IEventPublisher` est un service KEYED** (clé = nom du DbContext). `HouseholdWriteContext` le
   prenait sans `[FromKeyedServices]` : l'hôte refusait de démarrer sur la validation du conteneur.
   Build et 4 100 tests passaient.
2. **`PepStatus.NotExposed` était une instance statique PARTAGÉE** — et le type est mappé comme
   owned entity. Deux leads pointaient donc sur le même objet, et la seconde sauvegarde échouait
   avec *« The property 'Lead.PrincipalPep#PepStatus.LeadId' is part of a key and so cannot be
   modified »*. Corrigé en propriété expression-bodied : une instance neuve à chaque accès. Mesuré,
   pas théorisé — cassé dès que deux leads passaient par un même DbContext.

### Un nettoyage que le lot a imposé
`db.Leads.Update(lead)` a été **retiré de sept handlers** (`SetQualificationCompleteness`,
`UpdateLeadOwner`, `ReturnLeadToQueue`, `RecycleLead`, `SetIntentLevel`, `ReopenLead`,
`RecordFirstContact`). Tous chargeaient déjà en `AsTracking()`, donc l'appel était redondant — et il
est devenu nuisible : `Update` marque la clé identifiante du owned `PrincipalPep` comme modifiée, et
EF refuse la sauvegarde. C'est le piège `db.Update` déjà documenté, qui vient de s'étendre aux owned
types.

### Une revue de sécurité a trouvé une asymétrie lecture/écriture (corrigée)

`GetHouseholdHandler` chargeait le lead par id **sans contrôle de propriété**, alors que les
écritures passent toutes par `LeadAccessGuard`.

**Ce que ce n'était pas :** une faille inter-tenant. La requête n'utilise pas
`IgnoreQueryFilters()`, donc le filtre global de `LeadsDbContext` s'applique, et l'endpoint exige
`lead:read`. Et c'était la convention du module — **aucune** lecture de lead de M13 n'est scopée à
l'agence, `GetLeadHandler` fait exactement la même lecture par id.

**Ce que c'était quand même :** cette lecture renvoie l'identité d'un conjoint et d'enfants — noms,
dates de naissance, adresses — plus une classification PPE. C'est précisément la donnée que ce lot
chiffre et met derrière une permission. « N'importe quel collègue peut VOIR le foyer qu'il ne peut
pas MODIFIER » rendait la garde d'écriture décorative. Le tenant + `lead:read` est la bonne posture
pour un numéro de téléphone, pas pour ceci.

**Corrigé** en appelant `EnsureCanWorkAsync` sur la lecture, avec `LEAD_NOT_YOURS` → 403 via le
même `Problem()` que les écritures. Sûr malgré l'échec fermé de la garde : les rôles de contournement
et la propriété **directe** sont vérifiés AVANT l'appel M12, donc le porteur du lead ne l'atteint
jamais — seul un superviseur non vérifiable est refusé pendant une panne, et le refuser est correct.
Dix cas neufs épinglent le périmètre, les deux comportements de panne et la porte PPE.

### Reste à faire avant le lot 4
1. `dotnet ef database update --context LeadsDbContext`.
2. Régénérer le client front : 6 endpoints neufs.
3. **Configurer `HouseholdEnabled` sur les lignes de produit concernées.** Toutes portent `{}`
   depuis le lot 2, donc toute saisie de foyer sur une ligne résolue est refusée par
   `HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` — c'est le défaut voulu, pas une régression, mais il faut
   le lever produit par produit.
4. Décider si `lead:household:manage` et `lead:pep:read` sont attribués à d'autres rôles que
   System/Administrator (que `RoleSeeder` sert automatiquement).

---

## 13. Lot 4 — ce qui a été livré (2026-10-10)

**BNF-B-07 ✅, BNF-B-08 ✅, BNF-B-09 ✅.** 4 251 tests verts (63 neufs), `dotnet build` 0 erreur,
`--emit-openapi` 344 paths, une migration avec backfill.

### Le questionnaire (B-07)
`QualificationTemplate` gagne `SubjectScope`, `IsHealthQuestionnaire`, `ExcludeFromScoring`. Le
défaut reste `SubjectScope.Lead` : **tout questionnaire écrit avant cette évolution se comporte
exactement comme avant**, ce que la story exige.

`ExcludeFromScoring` est **forcé** à vrai pour un questionnaire santé, pas simplement mis par
défaut : une case qu'un administrateur pourrait décocher ferait de l'interdiction une configuration.

**Trois catégories d'assurance, pas une.** Le `ProductCategory` du Kernel éclate l'assurance en
`Insurance`, `HealthInsurance` et `ForecastInsurance` — lire le « catégorie Insurance » de la story
comme le seul membre de ce nom aurait refusé le produit même pour lequel un questionnaire santé est
écrit. La règle est aussi revérifiée dans `UpdateDetails` : ce chemin ne passe pas par
`SetSubjectScope`, et un questionnaire santé déplacé sur une ligne d'épargne continuerait de
collecter sous un produit qui n'a rien à demander.

### Une réponse par personne (B-08)
Identité d'une réponse = `(lead, template, subject type, subject id)`, tenue par
`ux_qualification_responses_subject`. Un questionnaire *lead-scoped* stocke l'**id du lead** comme
sujet, pas un null — PostgreSQL traite chaque null comme distinct, donc la contrainte n'aurait rien
contraint.

`ExpectedResponsePlanner` crée les lignes `ToComplete` **dans la transaction de la modification du
foyer** : un membre ajouté dans une transaction et son questionnaire créé dans une autre, c'est une
fenêtre où le membre existe et ne doit répondre à rien — précisément l'« assuré oublié » que la story
existe pour empêcher. Idempotent par diff, et l'index unique est ce qui tient sous deux requêtes
concurrentes, ce qu'aucun diff ne peut faire.

Le **retrait d'un membre ne supprime rien** : le planificateur ne lit que les membres **actifs**,
donc l'attente disparaît et la ligne reste.

**`QuestionnaireVersion` est figé à la création.** Publier une révision incrémente la version du
template ; une réponse relue à travers le template vivant serait réinterprétée contre des questions
que la personne n'a jamais vues — pour une déclaration de santé, c'est la différence entre une
réponse valide et une fausse déclaration.

### La santé (B-09)
**Deux colonnes, pas un convertisseur conditionnel.** `answers` reste du jsonb clair ;
`health_answers_enc` est chiffré en bloc. Un `ValueConverter` ne voit que la valeur, jamais la
ligne, donc une colonne ne peut pas être « jsonb sauf si cet autre champ dit le contraire ». Deux
colonnes rendent aussi la direction dangereuse impossible : une réponse mal classée ne peut pas
déposer des réponses santé en clair dans une colonne jsonb que quelqu'un indexera plus tard.
**Aucun blind index** : rien ne cherche une personne par la réponse qu'elle a donnée sur sa santé, et
un index ferait de « qui a répondu oui à la question 7 » une requête.

Consentement **par personne** et **actif au moment de l'écriture** : un consentement pour le
titulaire ne couvre pas un enfant, et un consentement retiré bloque la collecte — c'est tout
l'intérêt de pouvoir le retirer. Modélisé comme `ConsentType.HealthData` + `SubjectType`/`SubjectId`
sur `LeadConsent`, pas comme une entité à part : la machinerie (statut, canal, preuve, retrait tracé)
existait déjà, et une entité séparée aurait sorti le consentement santé des écrans que l'agent
utilise.

`HealthAnswerAccess` est **la seule porte** vers les réponses en clair, et `DecryptedHealthAnswers`
existe pour être **nommable** : un `IReadOnlyList<(Guid, string)>` aurait été indiscernable de
n'importe quelle autre liste, donc aucune règle d'architecture n'aurait pu le désigner.

`PurgeHealthResponsesJob` compte depuis la **transmission**, pas depuis la création : tant que les
réponses n'ont pas atteint l'assureur elles sont la seule copie. Conséquence assumée : une
transmission qui n'arrive jamais laisse la donnée. C'est la bonne panne — l'alternative est de
détruire une déclaration que personne n'a reçue. Une rétention à 0 est traitée comme « non
configurée » plutôt qu'honorée.

### Le test d'architecture, enfin complet
Le lot 0 avait noté ce qu'il ne pouvait pas exprimer. C'est fait : **rien hors de
`Features/Questionnaires` ne dépend de `DecryptedHealthAnswers` ni de `HealthAnswerAccess`**, plus
les deux règles nominales sur le scorer et le dispatching.

Et un **garde contre une règle creuse** : « rien hors de X ne dépend de Y » passe trivialement si
plus rien ne dépend de Y — un type renommé, une feature supprimée, une faute dans la constante de
namespace. Un cas assert que **dans** la tranche la dépendance existe, donc que l'exclusion porte
quelque chose.

### Deux bugs réels trouvés, dont un qui aurait cassé le lot 3 en production

1. **`LeadHouseholdMember` ajouté à un lead RECHARGÉ était tracé `Modified`, pas `Added`.** EF
   considère une clé `Guid` déjà renseignée comme générée par le store, donc « elle existe » — et
   `SaveChanges` tentait un UPDATE sur une ligne jamais insérée (*« Attempted to update or delete an
   entity that does not exist in the store »*). **C'est exactement ce que fait l'endpoint
   `AddHouseholdMember`** : il aurait échoué sur chaque lead existant. Les tests du lot 3 l'ont
   manqué parce qu'ils ajoutaient des membres à un lead lui-même neuf, où tout le graphe est `Added`
   et l'état est hérité. Corrigé par `ValueGeneratedNever()` sur la clé — le piège
   `ValueGeneratedOnAdd` déjà documenté, qui vient de s'étendre aux navigations de collection.
   Un test de régression assert l'**état** autant que la sauvegarde.
2. La migration générée voulait écrire `""` dans trois colonnes d'énumération et `0001-01-01` dans
   `created_at`. Corrigée à la main : `"Lead"`, `"Completed"`, `defaultValueSql: "now()"`, plus deux
   `UPDATE` de backfill (`subject_id = lead_id`, `created_at = answered_at`) **avant** la
   construction de l'index unique. `subject_scope` sortait aussi en `integer` — il manquait
   `HasConversion<string>()`.

### Une revue de sécurité a trouvé une atteinte à l'intégrité de l'audit (corrigée)

`RecordHealthConsentHandler` écrivait `recordedBy: cmd.GivenById ?? currentUser.Id`.

`LeadConsent.RecordedBy` est documenté comme « l'**utilisateur** qui a enregistré ce
consentement » — un champ d'audit qui nomme l'agent. En laissant un `GivenById` fourni par
l'appelant, et non validé, l'écraser, un agent authentifié pouvait **s'effacer** du seul document
qu'un régulateur demande en premier, ou l'attribuer à un collègue.

**C'était aussi un écart à la spécification.** BNF-B-09 énumère
« HealthConsent : SubjectId, **GivenBy**, GivenAt, Proof » — `GivenBy` est un champ **distinct**, et
je l'avais confondu avec `RecordedBy`. Corriger la faille comble donc aussi un champ manquant.

**Corrigé** : `LeadConsent.GivenBy` (colonne neuve, nullable, migration additive) ; `RecordedBy`
vaut **toujours** `currentUser.Id` ; et le donneur est validé par **la règle existante**
`HealthConsentGuard.EnsureRespondentValid` — obligatoire pour un mineur, puis le titulaire ou le
conjoint. Une seule définition de « qui peut agir pour un enfant », partagée avec le répondant de
B-08 plutôt que réécrite. Dix cas neufs épinglent la séparation et les refus.

### Reste à faire avant le lot 5
1. `dotnet ef database update --context LeadsDbContext` — **relire le backfill d'abord**, c'est la
   première migration de cette évolution qui touche des lignes existantes.
2. Régénérer le client front : 3 endpoints neufs.
3. Le score F13.6 **n'est pas** modifié par un questionnaire par personne dans ce lot : un
   questionnaire santé ne peut pas y toucher par la loi, et un non-santé par personne n'a pas de
   règle d'agrégation définie — en inventer une aurait été inventer une règle de scoring. Le score
   stocké sur une réponse par personne est informatif.
4. `lead:health:read` n'est attribué à aucun rôle d'agence par défaut (c'est voulu) ; décider qui
   l'obtient.

---

## 14. Lot 5 — ce qui a été livré (2026-10-10)

**BNF-B-11 ✅.** 4 303 tests verts (39 neufs), `dotnet build` 0 erreur, `--emit-openapi` 346 paths,
une migration additive `AddFatcaDeclarations` (table neuve, donc pas de backfill).

### Le statut est calculé, et l'ordre d'évaluation EST la règle
`FatcaDeclaration.ComputeStatus` — personne ne choisit un statut FATCA dans une liste déroulante ;
il découle de ce qui a été déclaré et de ce qui a été produit. Un champ modifiable laisserait un
opérateur enregistrer « NonUsPerson » par-dessus une naissance aux États-Unis déclarée.

1. **Refus → `Recalcitrant`**, quoi qu'il en soit par ailleurs.
2. **W-9 → `UsPerson`, même sans aucun indice.** Un indice est un *indicateur* ; un W-9 est la
   personne qui énonce le fait, et une déclaration l'emporte sur l'absence d'indication.
3. **Aucun indice → `NonUsPerson`**, y compris quand un W-8BEN a été produit quand même :
   `IndiciaCured` affirmerait que des indices ont été levés qui n'ont jamais existé.
4. W-8BEN contre des indices déclarés → `IndiciaCured`.
5. Sinon → `IndiciaPending`.

**`NotApplicable` n'est jamais stocké.** C'est une propriété du *tenant*, pas de la déclaration :
le stocker voudrait dire réécrire toutes les déclarations quand l'interrupteur bouge, et les
recalculer depuis des données inchangées quand il revient. `EffectiveStatus(fatcaEnabled)` est le
chemin de lecture ; les déclarations collectées pendant que FATCA était actif sont **conservées**,
pas réinterprétées.

### Les sept indices restent distinguables un par un
Pas un drapeau « hasIndicia » : la pièce qui **lève** un indice dépend duquel il s'agit (passeport
non américain, certificat de perte de nationalité, explication écrite), donc le responsable
conformité qui relit une levée doit savoir ce qui était levé. `FatcaIndicia.Declared()` rend les
noms — ce qu'un écran affiche et ce qu'une ligne d'audit peut porter sans porter la personne. Un
test compte les propriétés booléennes, garde contre l'oubli d'un indice dans un refactor.

### Ce que chaque documentation exige
| Documentation | Exige | Pourquoi |
|---|---|---|
| `FormW9` | un **TIN américain** (chiffré) | sans lui, la personne est déclarable et il n'y a rien sous quoi la déclarer |
| `FormW8Ben` | une **pièce justificative** | le formulaire seul affirme la non-américanité ; la pièce est ce qui lève l'indice qui la contredisait |
| `Refused` | un **motif** | un refus est une issue déclarable avec ses obligations propres, pas une absence |
| tout sauf `Refused` | **auto-certification signée et datée** (F02.10) | y compris un « aucun indice » : cette affirmation négative est exactement ce qu'une institution doit pouvoir produire |

Le `SignedAt` est la date de **signature**, pas celle de la requête ; une signature future est
refusée.

### Historisation, pas édition
Un changement de circonstances crée une **nouvelle** déclaration et supersède l'ancienne
(`SupersededAt` + `SupersededByDeclarationId`). Un régulateur qui demande ce que cette personne a
certifié en mars doit obtenir la réponse de mars. L'ancienne est superseded **avant** l'ajout de la
nouvelle, donc l'index unique partiel `ux_fatca_declarations_active_per_subject` ne voit jamais deux
déclarations actives — l'ordre compte, pas seulement la paire. Testé sur le **compte** de lignes
actives, puisque EF InMemory n'honore aucun index unique.

### Le périmètre et l'interrupteur tenant
`FatcaSubjectPlanner` : le titulaire **toujours**, plus le foyer actif **si** `FatcaForCoHolders`.
Dans un lead il n'y a pas encore de compte et donc pas de co-titulaire au sens bancaire — les
personnes qui y seront sont le foyer, c'est donc à elles que le drapeau étend la collecte.

**FATCA désactivé refuse l'écriture** plutôt que de l'accepter en l'ignorant : une déclaration
détenue par une institution sans obligation est un TIN américain conservé sans base légale. Et une
**panne des paramètres de conformité refuse** au lieu de deviner — deviner ON collecterait un TIN
sans obligation de le détenir, deviner OFF sauterait une déclaration légalement requise. Ni l'un ni
l'autre n'est acceptable, donc ni l'un ni l'autre n'est fait.

### L'événement
`FatcaStatusChanged` porte le **statut et le sujet, jamais le contenu** : pas de TIN, pas de
référence de document, pas la liste des indices. Un test sérialise la charge utile et vérifie
qu'elle ne contient ni le TIN ni une URL de stockage. `RequiresComplianceReview` est porté comme
**drapeau** pour que le consommateur n'ait pas à savoir quels statuts le déclenchent — c'est une
règle de ce module. Un statut **inchangé n'annonce rien** : renvoyer l'événement à chaque
re-certification identique est un bruit que le responsable conformité apprend à ignorer.

### Le lot 4 avait une lacune, trouvée et corrigée hors de cette session
`ux_qualification_responses_subject` était ajouté **sans étape de déduplication** dans la même
migration, et `QualifyLeadHandler` gardait son `Add` aveugle. La migration aurait donc échoué en
`23505` sur toute base où un lead avait été qualifié deux fois avec le même template, et la
re-qualification aurait renvoyé 500 ensuite. Les deux moitiés sont corrigées ; la déduplication
**exclut délibérément les réponses santé**, pour qu'une déclaration de santé dupliquée fasse échouer
l'index plutôt que d'être détruite silencieusement — meilleur garde-fou que ce que j'aurais écrit.
C'était un défaut réel dans ce que j'ai livré au lot 4.

### Reste à faire avant le lot 6
1. `dotnet ef database update --context LeadsDbContext` (deux migrations en attente : lot 4 et lot 5).
2. Régénérer le client front : 2 endpoints neufs.
3. **Activer FATCA sur les tenants concernés** (`PUT api/v1/compliance-settings`) — défaut `false`,
   donc aucune collecte n'est possible avant.
4. Brancher la consommation de `FatcaStatusChanged` côté M08 : l'événement part, personne ne
   l'écoute encore.

---

## 15. Lot 6 livré — enrichissement, complétude et conversion (BNF-B-04, B-12, B-23, B-24)

### Un seul calculateur, et c'est tout l'intérêt de l'histoire
`LeadCompletenessCalculator` (`Features/Enrichment/`) est la **seule** définition de « ce qui
manque ». Le pourcentage affiché sur la fiche, la liste par personne, la réponse de
l'enrichissement et le **verrou de conversion** sont la même exécution. Avant le lot 6, le verrou
ne vérifiait que les questionnaires : un champ requis manquant ou une déclaration FATCA absente le
traversaient, et un écran qui aurait calculé son propre pourcentage aurait pu afficher 100 % sur un
lead que la conversion refusait — le pire résultat possible pour un indicateur d'avancement.

Tout ce qu'il exige est **configuré** : les champs par `RequiredFieldsBeforeConversion` de la ligne
de produit, FATCA et la fenêtre PEP par `compliance_settings`, les questionnaires par les templates
attachés à la ligne. Une **panne rend un lead INCOMPLET, jamais complet** : chaque lecture
inter-module retombe sur un écart `COMPLETENESS_CHECK_UNAVAILABLE` qui bloque. Répondre « complet »
parce que M12 était injoignable est le seul mode de défaillance qu'un verrou ne doit pas avoir.

### Les codes de champ sont une liste fermée, dans le Kernel
`PersonFieldCode` (`Shared.Kernel/ValueObject/`) : 14 codes, chacun avec la portée des personnes à
qui il peut être demandé. Deux modules doivent s'accorder dessus sans pouvoir se référencer — M12
les stocke et **refuse désormais un code inconnu** à l'écriture
(`BENEFICIARY_POLICY_UNKNOWN_REQUIRED_FIELD:<code>`), M13 les évalue contre un lead et son foyer.
Avant, c'était du texte libre : une faute de frappe dans la configuration d'un produit était une
exigence qui **ne s'appliquait silencieusement jamais**, ce qui est le pire défaut imaginable pour
une liste dont l'unique raison d'être est de verrouiller une conversion.

`PersonFieldScope.PrincipalOnly` existe parce qu'il n'y a pas de colonne pour le téléphone, le
courriel, la pièce d'identité ou le montant souhaité d'un conjoint : exiger ces champs du foyer
produirait un manque que personne ne peut combler. Et `RequiredFieldEvaluator` **refuse par défaut**
sur un code qu'il ne gère pas (un `_ => false` commenté) : une exigence que rien n'évalue doit
bloquer, pas passer.

### Un écart d'adresse héritée pointe vers le titulaire
Un membre qui hérite de l'adresse n'a pas de champs postaux propres — l'héritage est un lien, pas
une copie — et sa propre commande de mise à jour les refuse
(`HOUSEHOLD_MEMBER_NEEDS_OWN_ADDRESS`). L'écart porte donc `ActionHint = "FIX_ON_PRINCIPAL"`, sinon
l'écran demanderait à l'agent de remplir un champ que l'API rejette. Symétriquement, les écarts
d'une fiche client portent `"FIX_ON_CUSTOMER"`, ce que BNF-B-23 demande explicitement.

### Le titulaire a enfin une identité complète (BNF-B-02)
Cinq colonnes ajoutées sur `leads` : `principal_usage_name`, `principal_address_enc` (chiffrée),
`principal_postal_code`, `principal_city`, `principal_country_code`. Sans elles,
`PersonIdentity.WithAddressOf` n'avait **rien à copier** : tout membre héritant de l'adresse se
résolvait sur une adresse nulle, c'est-à-dire exactement ce que la complétude aurait signalé comme
donnée manquante.

Une asymétrie est **assumée et documentée** sur `Lead.PrincipalIdentity` : la date de naissance du
titulaire reste en clair (`date_of_birth`, colonne antérieure lue par les importateurs, le scoreur
de doublons et plusieurs projections), alors que celle d'un membre du foyer est chiffrée. La
chiffrer est une migration à travers tout cela, hors périmètre — mieux vaut le nommer que le laisser
découvrir.

### « Non exposé » et « jamais demandé » ne sont plus le même état
`Lead.PepDeclaredAt` et `LeadHouseholdMember.PepDeclaredAt`, nuls sur toutes les lignes existantes —
ce qui est **exact**, pas une lacune de reprise : personne n'a rien déclaré à leur sujet.
`PepStatus.NotExposed` est le défaut d'un type possédé (EF ne sait pas distinguer un propriétaire
absent d'un propriétaire tout-nul), donc le statut seul ne pouvait pas porter la différence, et un
dossier LBC/FT qui ne distingue pas une réponse négative d'une question non posée n'a pas de
réponse. Le code `PEP_NOT_DECLARED` s'appuie dessus ; une déclaration explicite « non exposé » le
satisfait.

Le marqueur du membre est **passé explicitement** par `Lead.AddHouseholdMember`, pas déduit de
`pep is null` : l'appelant fournit toujours un statut (le défaut propagé en est un), et seul
l'appelant sait si quelqu'un a réellement répondu. Un titulaire non exposé propage « non exposé »,
ce qui n'est pas une réponse à propos du conjoint.

### `EnrichLeadCommand` : identité et ligne de produit, rien d'autre
Chaque champ optionnel, **null = ne pas toucher**, vide = effacer. L'enrichissement se fait sur
plusieurs entretiens depuis un écran qui ne poste que ce qu'il a collecté ; un null qui effacerait
ferait écraser chaque sauvegarde par la suivante. Ce qui est **délibérément absent** : le statut PEP
(sa propre commande — déclarer une exposition `Self` reclasse tout le foyer et la ligne d'audit doit
le dire), le courriel (un seul mutateur, `SetEmail`, qui maintient l'index aveugle), le foyer, les
questionnaires et FATCA (chacun sa tranche, chacun sa règle de consentement ou de conformité).

La ligne de produit est le **seul champ validé côté serveur** ici, parce qu'elle décide quelle
politique gouverne le lead : un id non vérifié est précisément ce que son absence des contrats de
capture sert à empêcher. Une panne M12 **refuse** l'écriture plutôt que de stocker un id non
validé.

`HouseholdWriteContext` a été scindé : `LoadLeadAsync` (chargement + péremption + propriété +
fenêtre PEP) et `LoadAsync` (= le précédent + les règles de foyer de la ligne). L'enrichissement
utilise le premier, car le second refuse par `HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` sur une ligne qui
n'enregistre pas de foyer — correct pour ajouter un conjoint, absurde pour écrire l'identité du
titulaire d'un compte courant. Deux méthodes plutôt qu'un drapeau : un booléen de porte est un
booléen qu'un appelant passe un jour dans le mauvais sens.

### Conversion : deux chemins, et l'idempotence (BNF-B-12, B-24)
`ConvertLeadHandler` branche sur `Lead.Kind` :

| | Nouveau prospect | Client existant |
|---|---|---|
| Création M01 | `CreateFromLeadAsync` | **aucune** (`lead.CustomerId`) |
| Verrou de doublons | appliqué | **sauté** |
| `ConversionType` | `NewCustomer` | `ExistingCustomerNewProduct` |

Le verrou de doublons est sauté parce qu'il cherche d'autres **leads** correspondant à la même
personne, et que pour un client qui demande un second produit ce sont l'état normal des choses — la
fiche client les liste comme demandes en cours. Bloquer dessus rendrait toute seconde souscription
impossible. La question de doublon qui s'applique vraiment à un tel lead est « ce client détient-il
déjà cette ligne », c'est-à-dire `AllowMultipleHoldings` contre les détentions : lot 7.

**Idempotence** : `Lead.Convert(customerId)` acceptait silencieusement une seconde conversion,
re-horodatait `ConvertedAt` et **republiait** `LeadConverted` — un back-office se faisait donc dire
deux fois d'ouvrir la même détention. Convertir vers le **même** client réussit sans rien changer
(`AlreadyConverted: true`, aucun événement) ; vers un **autre** est refusé
(`LEAD_ALREADY_CONVERTED_TO_ANOTHER_CUSTOMER`). De même, nommer un client différent de celui auquel
le lead est rattaché est un conflit (`CUSTOMER_LINK_CONFLICT`) et non un choix à honorer en
silence : l'un des deux enregistrements est faux, et en choisir un convertirait la demande contre
une fiche que l'agent n'a jamais relue.

Les événements s'enrichissent : `LeadConverted` porte `ConversionType` et `ProductLineId` (un
consommateur ne peut dériver ni l'un ni l'autre — les deux chemins finissent sur un lead pointant
vers un client) ; `KycRequested` porte `HasPepExposure`, **le drapeau et seulement le drapeau** — la
fonction, le pays et le lien familial restent derrière `lead:pep:read`.

Côté M02, le niveau de vigilance est **relevé, jamais abaissé**, et il est appliqué aussi sur le
chemin « le fichier existait déjà » — qui est ici le **cas normal**, puisque
`ClientCreatedKycConsumer` gagne généralement la course. N'appliquer le niveau qu'à la création
aurait donné un drapeau PEP sans effet dans le cas courant : le genre de lacune qui ressemble à du
code qui fonctionne.

### BNF-B-23 : ce qui est livré, ce qui ne l'est pas
Livré — **lecture** : pour un lead rattaché, l'identité du titulaire n'est **plus jamais
redemandée** (M01 en est la source), et à la place la fiche client est vérifiée : existence, statut
vendable, KYC approuvé et non expiré, palier KYC exigé par la ligne. Chaque manque bloquant pointe
vers l'action à mener sur la fiche client.

**Non livré, et explicitement reporté au lot 7** : l'**interdiction d'écriture** du foyer, du PEP et
de FATCA dans le schéma `leads` pour un lead rattaché. La règle est juste, mais M01 n'expose pas
encore de foyer (`ClientRelationship` arrive au lot 7) : refuser maintenant laisserait l'agent sans
aucun endroit pour enregistrer un foyer. La complétude lit donc encore les membres portés par le
lead, ce qui est une position transitoire et non la cible.

Et un **choix corrigé** : `QualificationSubjectType.CustomerPrincipal` /
`CustomerHouseholdMember` restent **non produits**. Basculer le type de sujet sur `Lead.Kind`
paraissait juste et ne l'est pas — un lead dont le titulaire avait déjà répondu en `Principal` puis
qui est rattaché à un client devrait instantanément une seconde réponse `CustomerPrincipal`, sa
ligne répondue ignorée et sa conversion bloquée. Le sujet d'un questionnaire posé **sur un lead**
est le titulaire de ce lead, quel que soit le propriétaire. Ces deux valeurs sont pour des
questionnaires attachés à la **fiche client**. Le commentaire du lot 4 qui annonçait « le lot 6
l'écrit » était faux et a été corrigé.

### Un écart assumé avec la spécification : `PRODUCT_LINE_NOT_RESOLVED`
§3.3 demande que la conversion soit **refusée** quand la ligne de produit n'est pas résolue. C'est
la bonne règle et elle est implémentée — mais derrière un paramètre tenant,
`LeadSettingKeys.RequireProductLineToConvert`, **défaut `false`**.

Pourquoi : `Lead.ProductLineId` est neuf, la résolution depuis `InterestedProduct` est au mieux
exacte et non floue, et `PullLeadSourceJob` comme `SystemCaptureLeadEndpoint` écrivent littéralement
`"Unknown"` quand une source ne nomme aucun produit — ce qui ne résout rien, par construction.
Livrer le refus actif par défaut rendrait donc **inconvertible, le jour du déploiement, tout lead
déjà en base** et tout lead venant d'une source non étiquetée. Ce n'est pas un gain de conformité,
c'est une panne.

L'écart est **signalé dans les deux cas** — c'est le premier élément du rapport, avec ou sans le
paramètre — donc personne ne le découvre plus tard. Activer le paramètre est l'étape d'exploitation
qui achève BNF-B-04, et elle vient **après** la configuration du catalogue et des libellés. Le
défaut est une ligne à changer si le PO en décide autrement.

### Tests (≈79 nouveaux, 2 752 au total, tout vert)
`PersonFieldCodeTests` (dont un cas qui échoue si une constante est ajoutée sans son entrée dans
`All`), `LeadCompletenessTests` (17 : ordre des écarts, le paramètre dans les deux sens, champs par
personne, portée principal-seul, adresse héritée satisfaite **et** manquante, PEP déclaré vs
supposé, arithmétique du pourcentage, FATCA off non compté / on exigé / documentation en attente non
bloquante, deux pannes), `ExistingCustomerCompletenessTests` (16 : pas de ressaisie, le contraste
avec le même lead non rattaché, cinq statuts KYC, trois statuts client, palier exigé, panne M01),
`EnrichLeadHandlerTests` (12 : écriture, incrémentalité, effacement par vide, ligne inconnue, panne
M12, activité d'un autre lead, l'événement ne porte que des **noms** de champs, lead converti, lead
d'autrui, ligne sans foyer), `ConversionBranchTests` (11), `PepDeclarationTests` (6),
`PrincipalIdentityTests` (6), plus 4 cas de vigilance KYC côté M02.

Deux tests pinnent explicitement une **régression de ma propre livraison** : `A_line_that_records_no_household_does_not_refuse_an_identity_write` (la scission de `HouseholdWriteContext`) et
`A_members_inherited_address_is_satisfied_by_the_principals` (les colonnes postales du titulaire).

### Reste à faire avant le lot 7
1. `dotnet ef database update --context LeadsDbContext` — **trois** migrations en attente
   (lots 4, 5 et 6). Celle du lot 4 contient une déduplication à relire avant application.
2. Régénérer le client front : 2 endpoints neufs (`GET leads/{id}/completeness`,
   `POST leads/{id}/enrich`), et **`ConvertLeadResult` a changé de forme** (`conversionType`,
   `alreadyConverted`, `completeness`).
3. **Configurer `RequiredFieldsBeforeConversion` par ligne de produit** : toutes les lignes portent
   `{}`, donc aucun champ n'est exigé nulle part et la complétude répond 100 % partout. C'est le
   geste qui donne un sens au lot.
4. Décider d'activer `require-product-line-to-convert` par tenant, après le point 3.
5. `POST leads/{id}/convert` renvoie désormais **422 avec le rapport** quand le dossier est
   incomplet, au lieu d'un 200 portant `customerId = Guid.Empty` — à vérifier côté front.

---

## 16. Lot 7 livré — foyer client, produits détenus, désignations (BNF-B-13, B-14, B-15)

### Le foyer client étend `ClientRelationship`, comme prévu au §3.2
Pas de `CustomerHouseholdMember`. La table modélisait déjà un conjoint et un enfant, en interne
(`RelatedClientId`) comme en externe, avec validité temporelle et réciprocité — et le retrait
logique que demande B-13 **était déjà** `ValidTo` + `CloseReason`. Le foyer est donc la vue filtrée
`IsHouseholdMember` : `Type ∈ {Spouse, Child, Dependent} ∧ ValidTo IS NULL`.

Ajouté : `SpouseUnionType`, `UsesPrincipalAddress`, `Pep` (type possédé, via le mapping partagé),
`PepDeclaredAt`, `SourceLeadMemberId`, et les champs d'identité qui manquaient pour qu'un membre
non-client soit une **personne entière**. Ces derniers sont des **colonnes plates plus une
projection** (`ResolveIdentity`), pas un `PersonIdentity` possédé — exactement la décision prise
pour `Lead.PrincipalIdentity`, et pour la même raison : `ExternalFullName`, `ExternalDateOfBirth` et
`EncryptedExternalDocumentNumber` sont déjà lus par l'endpoint de révélation, l'exécuteur de fusion
et les importateurs. `ExternalFullName` est **tenu à jour** par `ApplyIdentity`, sinon un conjoint
renommé garderait son ancien nom partout sauf sur l'écran du foyer.

`SpouseUnionType` a **déménagé dans le Kernel** : la conversion copie la valeur de M13 vers M01, et
deux enums par module seraient deux listes qui divergent — précisément ce que le §3.2 cherche à
éviter.

### L'index conjoint est en deux migrations, et la détection dit qui
`AddClientHouseholdAndHoldings` est **purement additive**. `AddOneSpouseConstraint` exécute d'abord
un bloc `DO` qui **nomme les fautifs** (`tenant X / client Y : 2 conjoints actifs`) et lève, puis
crée l'index. Sans cela le déploiement meurt sur `23505 could not create unique index
"uq_client_one_spouse"` — un message qui ne nomme ni le tenant ni le client, suivi d'un rollback où
personne ne sait quelles lignes corriger. **Rien n'est réparé automatiquement** : deux conjoints
actifs sont soit une faute de saisie soit une séparation non enregistrée, et seul un humain
tranche.

Détail qui a coûté deux itérations : EF identifie un index par son **jeu de propriétés**, donc
déclarer un second index sur `(tenant_id, client_id)` **remplace** l'index simple qui sert toutes
les lectures « relations de ce client ». Il faut la surcharge nommée `HasIndex(expr, name)` **et**
`HasDatabaseName`, sinon la colonne s'appelle `..._client_id1`.

### `Client.HasPepExposure` et la revue KYC
Trois colonnes neuves sur `clients` : `PrincipalPep` (possédé, préfixé, via le mapping partagé),
`PepDeclaredAt`, `HasPepExposure`. Le drapeau est **stocké** pour les mêmes raisons que côté lead :
un écran filtre dessus en SQL, et une **hausse** doit déclencher un événement au moment exact où
elle se produit.

`RecalculateHasPepExposure` retourne `true` **uniquement sur une hausse**, et c'est `CommitAsync` du
`HouseholdWriteContext` qui l'appelle sur **chaque** écriture du foyer — un handler qui l'oublierait
laisserait un client « non exposé » avec un conjoint exposé au dossier, le seul état qu'une revue
LBC/FT ne doit jamais voir. `ClientPepExposureRaisedEvent` ne porte **aucune** classification : le
consommateur ouvre une revue, il ne lit pas pourquoi.

Et le foyer client **n'a pas de plafond d'enfants**. `ClientHouseholdDto.MaxChildren` est toujours
nul : un plafond est une règle de **ligne de produit** et un client en détient plusieurs à la fois.
Le lead en a un parce qu'un lead est une demande pour une ligne. Le champ est conservé pour que les
deux écrans partagent une forme, et documenté pour que personne ne le remplisse avec la mauvaise
réponse.

### `ProductHolding` : la saisie CRM, pas un double des lectures d'Integration
`customers.product_holdings` avec la machine à états déclarative
(Draft → PendingSignature → PendingSync → Active | Rejected → Closed), `Specifics` en jsonb typé, et
`Origin` qui décide si le CRM peut écrire.

- **Les transitions sont une table, pas des comparaisons dans les handlers** — la leçon que
  `KycFile` a payée en M02.
- **`ConfirmSync` et `RecordRejection` n'ont pas d'endpoint** : c'est le back-office qui répond, et
  ça arrive par événement. Les méthodes existent sur l'agrégat, le consommateur du lot 8 les
  appellera.
- **La signature est revérifiée dans `SubmitForSync`**, pas déduite du statut : `Sign` ne déplace
  pas le statut, donc un holding peut être en `PendingSignature` signé ou non, et B-16 fait de la
  signature — non du statut — la condition du départ vers un back-office.
- **Un retour en correction efface la signature.** Ce que le client a signé n'est plus ce qui
  partirait ; la garder laisserait des conditions corrigées atteindre un back-office munies de la
  preuve du consentement aux précédentes.
- **`ImportedFromBackOffice` est en lecture seule**, et le refus est dans l'agrégat : la copie
  autoritaire est ailleurs, donc une modification CRM serait écrasée au prochain rapprochement sans
  que l'agent apprenne jamais que son changement est perdu.
- `Imported` est une **factory distincte**, pas un passage par `Subscribe` + transitions : un
  holding importé n'a jamais été un brouillon ici et n'a jamais été signé ici, et le faire marcher
  dans la machine inventerait une date de signature que rien dans ce système n'a constatée.

`HoldingSpecifics` est un **record plat** et non une hiérarchie polymorphe — c'est le risque R6
évité par construction : pas de discriminant, donc pas de schema filter Swashbuckle à se rappeler,
et le client Angular généré ne peut pas émettre un corps qui part en 422. Rien n'est dispatché
dessus et tout le monde lit l'enregistrement entier, donc le discriminant n'achèterait rien.

### `BeneficiaryDesignation` : la clause est un ensemble
Enfant de l'agrégat, parce que **tous** les invariants portent sur l'ENSEMBLE : la cardinalité d'un
rôle, et la somme des quotes-parts d'un rang. D'où `Designate` qui **remplace la clause entière** :
un endpoint « ajouter un bénéficiaire » refuserait chaque état intermédiaire (une clause à trois
bénéficiaires deviendrait impossible à saisir) ou accepterait un état invalide.

- **Somme à 100 % PAR RANG, jamais en travers** : le rang 2 n'hérite que si le rang 1 ne peut pas,
  donc chaque rang alloue la totalité pour son compte. Comparaison sur un `decimal` exact, aucune
  tolérance : 33,33 + 33,33 + 33,34 est l'arithmétique que l'agent tape, et arrondir cacherait une
  clause qui ne tombe pas juste jusqu'au sinistre.
- **Sur un holding `Active`, toute modification crée une VERSION** (avenant) : l'ancienne est
  clôturée par `ValidTo` et reste lisible. En brouillon, non — versionner chaque frappe
  enterrerait les vrais avenants.
- **`PendingSync` refuse toute modification de clause** : la demande est déjà dans une file, et une
  clause qui changerait dessous serait envoyée comme ni l'une ni l'autre version.
- **Le sujet est un couple opaque** (`SubjectType`/`SubjectId`) : un client, une ligne de
  `ClientRelationship`, ou personne du tout — une clause décès peut nommer un ami, et refuser de
  l'enregistrer jusqu'à ce qu'il devienne client rendrait la clause inenregistrable. La **même
  personne est désignable sur plusieurs produits** (B-15 l'exige), donc rien n'est unique par sujet.
- `ux_product_holdings_live_per_line` couvre la course de B-22, mais c'est le **handler** qui décide
  si la règle s'applique : `AllowMultipleHoldings` vit dans le catalogue M12 et un index partiel ne
  voit que sa table. Sans l'index la lecture du handler peut être doublée ; sans le handler l'index
  interdirait un second compte d'épargne, ce qui est parfaitement légitime.

### Un membre qui quitte le foyer (B-13)
`RemoveHouseholdMemberHandler` : clôture logique avec motif obligatoire, clôture du **miroir** d'un
conjoint client-à-client (sinon l'un des deux clients affiche encore un mariage que l'autre a
terminé), puis `FlagDesignationsOf` sur chaque holding concerné → `ToReview`, **jamais supprimé et
jamais réalloué**. La quote-part de 60 % reste 60 % : le rang ne totalise plus 100, et c'est
exactement ce qu'il faut montrer à l'agent. `HouseholdMemberRemovedEvent` ne part **que** si
quelque chose a été marqué — une suppression sans effet est de l'intendance ordinaire, et notifier
dessus apprendrait au destinataire à ignorer celles qui comptent.

### `HouseholdWriteContext` scindé, encore, et pour la même raison
`HoldingWriteContext` et `HouseholdWriteContext` sont **deux services distincts** de M13 et l'un de
l'autre : le périmètre ici est celui de M01 (hors périmètre ⇒ `CLIENT_NOT_FOUND`, jamais 403, pour
que l'existence ne fuite pas), et ce qu'ils chargent n'est pas la même chose. Ce qu'ils partagent
est la discipline : l'ordre des portes compte, et aucune écriture ne peut en sauter une.

### Une panne découverte par `--emit-openapi`, pas par les tests
Deux modules déclarent maintenant un `PersonIdentityDto` et un `PepStatusDto`, et l'id de schéma par
défaut de Swashbuckle est le nom court — donc **la génération entière du document levait** :
`Can't use schemaId "$PersonIdentityDto" … already used for type …`. Panne de démarrage, invisible
au build et aux tests.

`SwaggerSchemaIds` répond : nom court, **sauf** pour les noms courts qu'au moins deux modules
déclarent, qui reçoivent leur module en préfixe (`CustomersPersonIdentityDto`). Tout qualifier
aurait renommé **chaque** modèle du client Angular d'un coup — des centaines de ruptures pour une
collision. Quatre schémas ont changé de nom, aucun autre.

### Et le report du lot 6 est tenu
`ICustomersModule.CreateHoldingFromLeadAsync` + `CreateHoldingFromLeadHandler` ferment B-24 : une
conversion dont le lead porte une ligne de produit résolue laisse un holding **`Draft`** derrière
elle. Idempotent sur `SourceLeadId`, **best-effort** et après le commit du lead : la souscription
est une conséquence de la conversion, pas une condition — échouer la commande entière laisserait
l'agent avec un client, un lead converti et une erreur disant que rien n'a marché.
`ConvertLeadResult.HoldingId` est nul quand il n'y avait rien à ouvrir ou que M01 a refusé, et un
test épingle chacun des deux cas.

Les **désignations proposées** depuis le foyer du lead restent non livrées : `Designate` les
accepte dès maintenant (`SubscribeToProductCommand.Designations` et
`PUT holdings/{id}/designations`), mais les proposer automatiquement demanderait de mapper les
`LeadHouseholdMember` vers des `ClientRelationship` créés par la même conversion, ce qui n'existe
pas encore — la conversion ne recopie pas le foyer du lead. C'est le dernier morceau de B-13/B-24 et
il est nommé ici plutôt que supposé fait.

### Tests (86 nouveaux, 2 842 au total, tout vert)
`BeneficiaryDesignationRulesTests` (16 : parts, rangs, cardinalités, sujets, doublons),
`ProductHoldingLifecycleTests` (16 : le chemin heureux, la signature comme porte, les refus de la
machine, l'import en lecture seule, les bornes des `Specifics`), `ClauseVersioningTests` (8 :
avenant vs brouillon, l'ancienne version clôturée et lisible, le marquage d'un membre partant,
la même personne sur plusieurs produits), `ClientHouseholdTests` (17 : la vue filtrée, un
garant refusé, un seul conjoint, l'héritage d'adresse effacé à l'écriture, un membre qui est
client, la propagation PEP et son override, le détail masqué sans la permission),
`RemoveHouseholdMemberTests` (8), `ProductHoldingHandlerTests` (20 : le snapshot de ligne,
`AllowMultipleHoldings` dans les deux sens, la clause sur un holding **rechargé** — le piège
`ValueGeneratedNever` —, les événements en ids seuls, la pagination, le périmètre), plus 4 cas de
holding à la conversion côté M13 et 2 cas de permissions.

Deux tests valent d'être signalés. `A_clause_recorded_on_a_reloaded_holding_is_inserted_and_not_updated`
épingle le piège que ce module a déjà payé : les tests d'agrégat ajoutent des enfants à un parent
NEUF, où tout le graphe est `Added` — seul le chemin charger-modifier-sauver l'expose, et c'est
celui de tous les endpoints. Et `A_member_an_agent_classified_individually_is_not_overwritten` a
découvert en cours d'écriture que `PepStatus.NotExposed` **ne porte pas** de motif d'override : le
garde-fou ne protège qu'une exposition **déclarée**, ce que le test dit maintenant explicitement au
lieu de l'affirmer à côté.

### Reste à faire avant le lot 8
1. `dotnet ef database update --context CustomersDbContext` — **deux** migrations, dans l'ordre.
   Exécuter la requête de détection des conjoints en double **avant**, sur chaque base.
2. `dotnet ef database update --context LeadsDbContext` — trois migrations en attente (lots 4, 5, 6).
3. Régénérer le client front : **15 endpoints neufs**, et quatre schémas renommés
   (`PersonIdentityDto` → `CustomersPersonIdentityDto` / `LeadsPersonIdentityDto`, idem
   `PepStatusDto`). `ConvertLeadResult` gagne `holdingId`.
4. **Configurer `AllowedRoles` par ligne de produit** : toutes les lignes portent `{}`, donc aucune
   désignation n'est acceptée nulle part et `signature-request` passe sans rien exiger. C'est le
   geste qui donne un sens à B-15.
5. Décider de l'attribution des quatre permissions neuves
   (`customers:household_manage`, `customers:pep_read`, `customers:holding_manage`,
   `customers:designation_manage`).
6. Brancher un consommateur sur `ClientPepExposureRaisedEvent`, `HouseholdMemberRemovedEvent`,
   `ProductSubscriptionRequestedEvent` et `BeneficiaryDesignationChangedEvent` : les quatre partent,
   personne ne les écoute — c'est le lot 8.

---

## 17. Lot 8 livré — BNF-B-16, B-17, B-18, B-19, B-20

**Les cinq stories.** B-16, B-19 et B-20 d'abord ; B-17 et B-18 ensuite, dans une seconde passe —
§17.6 (initialement « ce qui n'est pas dans ce lot ») est conservé tel quel, remplacé par §17.9 et
§17.10 qui disent ce qui a réellement été fait.

### 17.1 BNF-B-16 — la chaîne de souscription, dans les deux sens
Le lot 7 publiait `ProductSubscriptionRequested` et personne n'écoutait : un holding atteignait
`PendingSync` et rien ne l'en sortait jamais. La chaîne est maintenant fermée.

**Aller** — `HoldingSubscriptionConsumer` (Integration) : l'événement ne porte que des ids, donc le
consommateur relit les conditions et la clause par `ICustomersModule.GetHoldingForSyncAsync`. C'est
délibéré des deux côtés : une clause bénéficiaire nomme qui reçoit un capital, et une ligne d'outbox
qui la porterait serait une seconde copie du contrat que rien ne purge jamais.

Il crée **une** `IntegrationCommand` et **rien d'autre n'est réécrit** : les retries à backoff, les
familles d'erreur, la file de rejet et le dispatcher d'INT-06 existent et font le travail. Ce qui a
changé dans l'existant est minimal et nommé :

- `InsurancePolicyPayload` gagne `CrmHoldingId` et `ClauseVersion` (optionnels, défaut rétro-compatible).
- `IdempotencyKeyFactory.ForHoldingSubscription(tenant, connection, holdingId, clauseVersion)` —
  et le commentaire qui disait « la police n'a pas d'id CRM, aucun module n'en détient » **était
  devenu faux** : `ProductHolding` en est un. Les deux cas sont désormais distingués au lieu d'être
  confondus. Sans la version dans la clé, un **avenant serait avalé comme un rejeu** et l'assureur
  ne l'apprendrait jamais.

**Retour** — il manquait un signal générique. `CreateCustomer` et `OpenAccount` s'annoncent avec les
ids externes dont la chaîne d'onboarding a besoin ; tout le reste tombait dans un `default:` muet, de
sorte que le module **propriétaire de l'entité** ne pouvait pas apprendre que son écriture avait
abouti. D'où `IntegrationCommandSucceededEvent`, pendant symétrique de
`IntegrationCommandRejectedEvent`, publié dans cette branche. Côté M01,
`HoldingSyncConfirmedConsumer` et `HoldingSyncRejectedConsumer` filtrent sur
`EntityType == "Policy"` et appellent `HoldingSyncService`.

Trois décisions de ce service méritent d'être lues :

- **Pas derrière MediatR**, contrairement à toutes les autres écritures de la zone. Les appelants
  sont des consommateurs d'un **autre** module, sous identité SYSTEM, à propos d'un holding dont le
  périmètre agence ne veut rien dire là-bas : `HoldingWriteContext` vérifierait le périmètre du
  consommateur et `AuditBehavior` enregistrerait SYSTEM comme auteur d'un changement fait par
  l'assureur. La traçabilité qui compte — `ExternalRef`, `SyncedAt`, la ligne de commande
  Integration — survit intacte.
- **Idempotent côté métier, pas seulement côté inbox.** Confirmer un holding déjà `Active` avec la
  **même** référence réussit sans rien changer ; avec une **différente** c'est refusé
  (`HOLDING_EXTERNAL_REF_CONFLICT`) : deux références externes pour un contrat signifient qu'un des
  deux systèmes a tort, et choisir un camp en silence est la seule chose qu'une réconciliation ne
  peut pas défaire. L'inbox ne couvre pas ce cas — deux messages distincts portant le même
  résultat — donc le service doit.
- **Un succès sans référence laisse le holding en attente.** `ExternalRef` est le seul lien entre
  les deux systèmes ; un holding `Active` sans référence est irréconciliable (B-17), ce qui est pire
  qu'un holding visiblement en attente.

La signature est **revérifiée dans le consommateur**, alors que la transition vers `PendingSync`
l'exigeait déjà dans la transaction de M01. C'est le dernier code qui tourne avant qu'un contrat
quitte l'institution, et il ne doit pas se fier à une parole de seconde main sur le consentement du
client. Un refus ici est bruyant : soit quelqu'un a changé la machine à états, soit l'événement est
forgé.

### 17.2 BNF-B-19 — l'anonymisation à 24 mois
`AnonymizeClosedLeadsJob`, récurrent par tenant sous identité SYSTEM, à 03h45 — **après** la purge
santé de 03h30 : celle-là blanchit des réponses transmises sur des leads de tout âge, celle-ci
détruit tout d'un lead clos depuis la période de rétention du tenant.

**Cinq tables, une transaction par lot** : le lead, ses membres du foyer, ses réponses de
qualification, ses consentements et ses déclarations FATCA. Toutes les cinq ou aucune — un lead dont
le nom est parti et dont le conjoint a gardé le sien n'est pas anonymisé, il est à moitié
anonymisé, et **rien ne reviendrait finir le travail** parce que le lead ne correspond plus à la
sélection.

Ce qui part est la liste complète sur `Lead.Anonymize`, et trois choix y sont explicites :

- **Les blind index partent aussi.** En laisser un permettrait de confirmer un numéro deviné contre
  une ligne dont les données sont censées avoir disparu — c'est-à-dire exactement un blind index qui
  fonctionne.
- **La classification PEP part.** C'est une donnée personnelle *et* un enregistrement de conformité ;
  le dossier qu'elle justifiait est clos depuis deux ans, et garder une exposition politique sur un
  lead détruit, c'est conserver le champ le plus sensible de tous sans finalité restante.
- **Le point GPS part** : relevé à un accueil, c'est le pas de porte du prospect.

Ce qui **reste** est l'entonnoir : source, campagne, score, statut, étape, agence, dates, et l'id —
pour qu'une ligne d'audit qui nomme ce lead résolve encore. Détruire cela ferait d'une règle de
rétention une panne de reporting.

**Un lead `ExistingCustomer` ne perd que ses données propres.** La fiche client a sa propre règle de
rétention, son propre job et ses propres preuves face au régulateur (B-23). Ce job ne tient
**aucune** référence vers M01 : l'isolation est structurelle, pas une condition qu'on pourrait
oublier. Le lien `CustomerId` survit — c'est de l'histoire d'entonnoir.

`lead-retention-months` (défaut **24**). Zéro ou négatif **saute le run** au lieu d'être honoré :
c'est ce qu'une faute de frappe dans les paramètres produit par accident, et l'erreur est
irréversible.

### 17.3 BNF-B-20 — ce qu'un écran peut proposer
`ProductHolding.AvailableActions` calcule, **depuis la table de transitions elle-même**, les noms
d'endpoints que le holding accepterait : `specifics`, `signature-request`, `signature`,
`submission`, `return-to-draft`, `closure`, `designations`. Un front n'a donc plus à
réimplémenter la machine à états, et la première fois que les deux divergeraient l'agent prendrait un
409 sur un bouton qui semblait disponible.

Deux subtilités portées par le code : `signature` est listé **par le statut** et non par la table,
parce que `Sign` ne déplace pas le statut (re-signer est la façon dont une correction est captée) ;
et `designations` suit les refus de `Designate` plutôt que la table, parce que la clause est
modifiable dans tous les états vivants **sauf** `PendingSync`.

`ClientHouseholdDto.CanEdit` complète l'ensemble côté foyer. Avec `HoldingDto.CanEdit` (lot 7) et
`LeadCompletenessDto.CanConvert` (lot 6), les trois écrans de l'évolution portent leur propre
réponse serveur.

### 17.4 Une collision découverte par le build, pas par un test
`HoldingSyncService` et `SubjectLabelResolver` sont passés **public** : les deux consommateurs
MassTransit sont instanciés par le broker, donc publics, et un type public ne peut pas prendre une
dépendance interne dans son constructeur. C'est signalé dans leurs commentaires pour que personne
ne « corrige » la visibilité plus tard.

### 17.5 Tests (49 nouveaux, 4 311 au total, tout vert)
`HoldingSyncOutcomeTests` (12 : acceptation, succès sans référence, rejeu même message et message
différent, seconde référence refusée, motif lisible avec famille et code, détail tronqué, rejet non
réécrit, commande d'un autre type ignorée **avant** l'inbox, id CRM inconnu non fatal, autre tenant
introuvable), `HoldingSubscriptionConsumerTests` (13 : une commande, payload chiffré avec les noms
de champs seuls, rejeu, avenant = second envoi, holding non signé jamais envoyé, ligne CRM-only,
holding disparu, tenant SYSTEM, tenant sans connexion, et trois cas de mapping dont la part nulle
rendue à 0 et jamais à 100), `AnonymizeClosedLeadsJobTests` (12, dont la vérification de la DoD :
sérialiser les cinq tables et asseoir qu'aucun nom, numéro ou référence n'y figure), et 8 cas de
`AvailableActions` — dont un qui parcourt chaque état et vérifie que **chaque action proposée est
bien acceptée**, le seul vrai risque d'une liste calculée.

### 17.6 Ce qui n'était pas dans la première passe *(conservé — voir §17.9 et §17.10)*

**BNF-B-17 — réconciliation des holdings et des désignations.** Rien n'est commencé. Ce qu'il faut :
étendre `IntegrationReconciliationRun` / `IntegrationReconciliationGap` aux `ProductHolding` et aux
désignations, en comparant par `ExternalRef` et par `ClauseVersion` ; **aucun écrasement
automatique** — l'arbitrage passe par `GapResolution`, exactement comme `CbsKycMismatchDetected` déjà
le fait pour un palier KYC ; et `IntegrationDiscrepancyDetected` vers M08. L'infrastructure de
réconciliation existe et ne doit pas être réécrite. La dépendance est satisfaite : `ExternalRef` est
désormais rempli par le chemin de retour de B-16, donc il y a enfin quelque chose à comparer.

**BNF-B-18 — contrat de capture v2 et SDK.** Rien n'est commencé. Ce qu'il faut : prénom/nom/email
séparés dans `CaptureLeadRequest` v2, foyer et questionnaires optionnels, **jamais de donnée de
santé sans preuve de consentement** (le garde-fou existe déjà, `HealthConsentGuard`) ; la v1 avec
`FullName` reste acceptée et mappée ; et `forms.min.js` republié en **version épinglée neuve** —
décision PO existante : les snippets épinglent la version exacte, donc promouvoir l'alias casserait
chaque site intégré.

### 17.8 Une fuite inter-tenant trouvée par la revue automatique, et corrigée

`HoldingSyncService.BirthDatesAsync` interrogeait `client_relationships` avec
`IgnoreQueryFilters()` **sans prédicat de tenant**. Le commentaire de classe affirmait que chaque
requête du service en portait un ; celle-là non. La règle du dépôt est explicite depuis longtemps —
« `IgnoreQueryFilters` se paire toujours avec un `TenantId ==` manuel » — et je l'avais énoncée dans
le fichier avant de l'enfreindre dessous.

La chaîne exploitable, en deux moitiés qui se renforçaient :

1. `ProductHolding.Designate` ne validait que la **forme** d'un sujet : n'importe quel Guid non vide
   passait comme sujet `HouseholdMember` ou `Client`. Un agent portant
   `customers:designation_manage` pouvait donc enregistrer une désignation vers un id arbitraire.
2. `BirthDatesAsync` lisait ensuite la date de naissance de cette ligne **filtres désactivés**, et
   `HoldingForSync` la portait dans la charge utile envoyée à l'assureur.

Soit une lecture inter-tenant d'une donnée personnelle, exfiltrée par un canal légitime. Les ids
sont des Guid v4, donc l'attaque aveugle n'est pas réaliste — mais **un id non devinable n'est pas
un contrôle d'accès**, et c'est précisément pourquoi la règle de pairage existe.

Les deux moitiés sont corrigées, et la seconde était la plus importante :

- **`DesignationSubjectValidator`** : un sujet `HouseholdMember` doit être un membre **actif du
  foyer de CE client**, un sujet `Client` doit être un client **de ce tenant** non archivé ni
  fusionné. Appliqué sur les deux chemins qui acceptent une clause — `DesignateHandler` et
  `SubscribeToProductHandler` —, sinon la souscription serait le contournement de la règle. Il n'est
  pas dans l'agrégat parce que décider si un id est un membre du foyer est une **requête**, et un
  agrégat n'interroge pas.
- **`BirthDatesAsync` porte maintenant les deux prédicats** : le tenant (la règle) et le client (une
  date de naissance n'est jamais nécessaire que pour un membre du foyer de ce holding).

Le validateur corrige aussi un bug de justesse qui n'avait rien à voir avec la sécurité : un sujet
pendant arrivait chez l'assureur comme un **bénéficiaire sans nom** (`"—"`) sur une clause décès.

Dix cas l'épinglent (`DesignationSubjectScopingTests`), dont deux en défense en profondeur : une
désignation étrangère **forcée par l'agrégat** en contournant le handler ne rend plus ni la date de
naissance ni le nom, et le cas contraire — un vrai membre rend bien sa date — pour que le premier
ne passe pas parce que la fonctionnalité est cassée.

### 17.9 BNF-B-17 — la réconciliation des holdings
**Une seconde comparaison dans le MÊME job**, pas un second job. Elles ne peuvent pas être
fusionnées — une connexion core-banking se compare à `cbs_customer_snapshot` et une connexion
assurance au read model des polices, et réconcilier l'une contre la source de l'autre déclarerait
tout son portefeuille `MissingInExternal` — mais elles **partagent** le registre, la
déduplication, la clôture automatique et la ligne de run. D'où un job, deux comparaisons.

Le côté CRM est énuméré depuis `integration_reference` avec `EntityType = Policy`, exactement comme
le côté client : c'est la seule route qui existe (`ICustomersModule` n'expose aucune énumération) et
depuis B-16 le `CrmId` d'une référence de police **est un id de holding**. Le côté externe est
`PolicyRecord`, lu **restreint à la connexion** — différence réelle avec le snapshot CBS, qui est
par tenant : un tenant a un seul core banking mais peut distribuer pour plusieurs assureurs, et la
police d'un autre assureur ne dit rien du portefeuille de cette connexion.

Trois types d'écart, et un délibérément absent :

- `MissingInExternal` — le CRM croit le contrat en vigueur et l'assureur ne l'a jamais confirmé.
- `StatusMismatch` — les deux se contredisent sur la mise en vigueur, **dans les deux sens** (le
  pire étant « le CRM croit que le client n'a rien et l'assureur prélève encore »).
- `DesignationOutOfSync` — **neuf** : le CRM porte une clause que l'assureur n'a jamais reçue. Un
  avenant enregistré pendant que l'assureur était injoignable, ou dont la commande a été rejetée et
  jamais renvoyée. Ça compte : la police paierait les anciens bénéficiaires.

**Ce qui n'y est pas**, et c'est une décision : « les bénéficiaires de l'assureur diffèrent des
nôtres ». Rien dans `PolicyRecord` n'enregistre la clause de l'assureur, donc cette comparaison n'a
aucune entrée — et plutôt que de déclarer un type d'écart qui ne pourrait jamais se déclencher, la
moitié mesurable de la question est rapportée. C'est exactement l'argument que `ReconciliationScope`
tenait déjà pour `MissingInCrm`.

**La version envoyée est vérifiée EXACTEMENT**, pas comptée. La clé d'idempotence est déterministe,
donc le job la recalcule pour le couple (holding, version courante) et cherche une commande
`Succeeded` portant cette clé — une recherche exacte, une requête par page. Compter les commandes
réussies sous-estimerait dès qu'un avenant intermédiaire a été rejeté et qu'un suivant est passé :
un faux écart sur un contrat en réalité à jour, c'est-à-dire précisément le genre de chiffre contre
lequel ce module argumente.

Les ensembles `Detected`/`NotDetected` sont devenus **par famille**, et c'est structurellement
nécessaire : l'ensemble détecté est **aussi le filtre de clôture automatique**, donc passer celui du
core banking à un run assurance clôturerait chaque `DesignationOutOfSync` dès la première nuit. Le
test de garde est lui aussi devenu par famille — et il a immédiatement trouvé une omission réelle :
un run core-banking ne déclarait pas qu'il ne peut pas chercher une divergence de clause.
L'endpoint du registre résout maintenant ses ensembles selon ce que l'appelant demande — la famille
d'une connexion nommée, sinon l'**union** des détectés et l'**intersection** des indétectables.

### 17.10 BNF-B-18 — le contrat de capture v2
**Un seul enregistrement, deux formes.** v1 envoie `FullName` ; v2 envoie `FirstName`/`LastName`
séparément, un foyer optionnel et des questionnaires optionnels, et peut omettre `FullName`. Pas
deux endpoints : les deux formes diffèrent par les champs optionnels renseignés, pas par leur
sémantique, et un `/capture/v2` parallèle dupliquerait vingt-huit champs qui doivent rester
identiques — la dérive est une certitude, et le jour où elle arrive un lead capturé par une route
se score différemment du même lead capturé par l'autre. Ce dont v2 avait besoin était que
`FullName` cesse d'être obligatoire : une **relaxation**, donc tout corps v1 reste valide.

**Le vrai blocage était ailleurs, et il est corrigé.** `IngestInboundLeadHandler` — la route sur
laquelle le SDK poste — **exigeait** `fullName` et refusait par `REQUIRED_FIELDS_MISSING:fullName`
un formulaire v2 qui portait manifestement le nom du prospect, alors que le même payload tiré de
l'API d'un partenaire était accepté : `PullLeadSourceJob` le dérivait déjà. Trois chemins d'accord
par coïncidence et un en désaccord. `LeadNameResolver` est maintenant la règle unique des **quatre**
chemins entrants, et le message de refus nomme les trois champs dont n'importe lequel suffirait.

Note d'honnêteté : en extrayant la règle j'ai d'abord écrit un commentaire affirmant que la
concaténation locale produisait un espace unique acceptant un lead sans nom. **C'était faux** —
vérifié : les quatre cas donnent le même résultat qu'avant. Le commentaire dit désormais la vérité,
à savoir qu'il s'agit d'une déduplication sans changement de comportement sur ces deux chemins.

**Le foyer et les questionnaires atterrissent dans la MÊME transaction que le lead.** Un foyer
enregistré par une seconde requête est un foyer qui peut échouer seul, laissant un lead dont la
famille est à ressaisir — et une déclaration de santé dans cette position est pire : le
consentement a été donné et les réponses ont disparu.

**Jamais de santé sans preuve de consentement**, et le refus arrive **avant** toute écriture.
`CaptureHealthConsentCheck` refuse tout questionnaire portant des **réponses** sans référence de
consentement, quel que soit son template. C'est un arbitrage assumé : quelques questionnaires non
sanitaires seront refusés faute d'une référence dont personne n'avait besoin. Le coût est un 422
qu'un intégrateur corrige une fois ; l'alternative révèle à un formulaire non authentifié quels
templates sont des questionnaires de santé, et lui permet de les sonder en soumettant des réponses.

Un template inconnu ou non publié **refuse toute la capture** : les questions d'un brouillon peuvent
encore changer, donc des réponses enregistrées contre lui sont des réponses à des questions que
personne ne peut reconstituer — et les abandonner en silence dirait à l'appelant que la déclaration
a été prise.

**SDK 1.2.0 publié**, version épinglée neuve et non une modification de 1.1.0 : chaque build est
immuable parce qu'un site qui utilise `integrity` épingle la version exacte et que changer les
octets sous lui casse la page. Rien dans `forms.js` n'avait à changer pour envoyer les champs
séparés — les champs qu'un formulaire poste sont ceux configurés sur la source — donc la version
existe pour donner aux sites un build épinglable en sachant que les champs séparés fonctionnent.

**Un défaut trouvé en chemin, et NON corrigé de ma main.** `1.1.0/forms.min.js` a été renommé en
`forms.js` par une modification **indexée mais non commitée** antérieure à cette session. Or le
seeder lit `forms.min.js` et l'endpoint sert `forms.min.js` : **1.1.0 est donc invisible aux deux** —
jamais enregistré, jamais servi, et rien ne journalise qu'il a été ignoré. Revenir sur le renommage
d'un travail en cours n'est pas à moi de le décider, donc 1.2.0 porte le nom attendu et
`ShippedSdkBuildsTests` encadre le problème : la garde exige le fichier pour **chaque** build, avec
`1.1.0` dans une liste d'exceptions nommée et motivée — plus un second test qui **échoue le jour où
1.1.0 est réparé**, pour que l'exception ne survive pas à sa raison d'être.

### 17.7 Reste à faire
1. `dotnet ef database update --context CustomersDbContext` — deux migrations du lot 7, dans
   l'ordre, **après** la requête de détection des conjoints en double.
2. `dotnet ef database update --context LeadsDbContext` — **quatre** migrations en attente
   (lots 4, 5, 6 et `AddLeadAnonymizationFlag`).
3. Régénérer le client front : `HoldingDto` gagne `availableActions`, `ClientHouseholdDto` gagne
   `canEdit`.
4. **Mapper chaque ligne de produit assurance vers sa connexion** (`IntegrationMapping`, domaine
   `Product`, `CrmCode` = l'id de la ligne) — sinon un tenant à plusieurs assureurs voit ses
   souscriptions refusées par `MappingMissing`, et un tenant à un seul assureur passe par le
   fallback. C'est le geste qui rend B-16 opérationnel.
5. Vérifier `lead-retention-months` par tenant avant le premier run de l'anonymisation : **elle est
   irréversible**, et le défaut de 24 mois va détruire des leads clos il y a plus de deux ans dès la
   première nuit.
6. Brancher un consommateur M08 sur `ClientPepExposureRaisedEvent` et
   `HouseholdMemberRemovedEvent` (lot 7) : les deux partent, personne n'écoute.
7. **Décider du sort de `wwwroot/sdk/1.1.0/forms.js`** (§17.10) : soit le renommer en
   `forms.min.js` et retirer `1.1.0` de `ShippedSdkBuildsTests.KnownUnservable`, soit supprimer le
   répertoire. En l'état le build est dans l'arbre et ne peut être ni enregistré ni servi.
8. **Mapper chaque ligne de produit assurance vers sa connexion** est désormais doublement
   nécessaire : B-16 en a besoin pour envoyer, et B-17 compare les références que l'envoi produit.
9. **Auditer les désignations existantes** avant de considérer §17.8 close : les lignes écrites
   avant le validateur peuvent porter un sujet pendant. Requête de détection —
   `SELECT d.id, d.holding_id, d.subject_id FROM customers.beneficiary_designations d
   JOIN customers.product_holdings h ON h.id = d.holding_id
   WHERE d.valid_to IS NULL AND d.subject_type = 'HouseholdMember'
     AND NOT EXISTS (SELECT 1 FROM customers.client_relationships r
                     WHERE r.id = d.subject_id AND r.tenant_id = d.tenant_id
                       AND r.client_id = h.client_id AND r.valid_to IS NULL);`
   Rien n'est réparé automatiquement : un sujet pendant est soit une faute de saisie soit un membre
   retiré, et seul un humain tranche (c'est la logique de `ToReview`).
