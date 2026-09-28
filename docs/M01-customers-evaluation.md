# Module M01 — Clients : évaluation de l'implémentation

Date : 2026-09-27 · Branche : `main` · Migration : `20260927193533_InitialCustomers`

## 1. Chiffres

| Mesure | Valeur |
|---|---|
| Fichiers de production | 343 (24 985 lignes) |
| Fichiers de tests | 76 (16 200 lignes) |
| Tests du module | **735 / 735** |
| Build solution | 0 erreur |
| Endpoints HTTP | 54, répartis en 8 groupes de routes |
| Tables créées | 19 · 59 index (13 uniques, 5 filtrés) |
| Jobs récurrents | 4 orchestrateurs globaux + 3 jobs à la demande |
| Permissions `customers:*` | 9, toutes utilisées par au moins un endpoint |

Non-régression : `Leads` 191/191, `Administration` 114/114, `Notifications` 76/76,
`Shared.Infrastructure` 53/53. `Workflow` 32/33 — échec **préexistant** et sans lien
(`ActivateTemplateHandler` appelle `ExecuteDeleteAsync`, non supporté par EF InMemory).

## 2. Évaluation story par story

Légende : **OK** = critères couverts et testés · **Réserve** = couvert avec un écart documenté
· **Non couvert** = ne peut pas l'être dans cet environnement.

### Epic 1 — Socle

| US | État | Détail |
|---|---|---|
| BE-01 Permissions & paramètres tenant | **Réserve** | Les 9 permissions sont dans `Permissions.All` avec `Module = ApplicationModules.Customers` ; le `RoleSeeder` de M12 les accorde donc automatiquement à `System` et `Administrator`. Seed idempotent vérifié par comptage avant/après, et une valeur modifiée par le tenant survit à un re-seed. **Écart** : les paramètres vivent dans le schéma `customers` (`customer_settings`), pas dans M12 — M12 n'expose aucun magasin de paramètres générique et l'isolation des modules interdit d'écrire dans son schéma. |
| BE-02 Contrat `ICustomersModule` | **OK** | `GetClientSummaryAsync` ne renvoie aucune donnée chiffrée. `ResolveClientIdAsync` suit la chaîne de fusion avec garde à 20 sauts contre les cycles, et renvoie le dernier maillon connu si la chaîne est rompue plutôt qu'un `null` trompeur. Tenant étranger ⇒ inexistant (`IgnoreQueryFilters` + prédicat `TenantId` explicite). `ExistsAndActiveAsync` est vrai pour `Active` uniquement. |
| BE-03 Numéro client | **OK** | Format tenant `{AgencyCode}-{YYYY}-{Seq:6}`. Compteur `client_number_sequences` avec jeton `xmin` : pré-check + `catch (DbUpdateConcurrencyException)` et jusqu'à 5 relectures. Le compteur n'est jamais décrémenté, donc un numéro archivé ou fusionné n'est jamais réattribué. |

### Epic 2 — Personne physique

| US | État | Détail |
|---|---|---|
| BE-04 Créer un client PP | **OK** | `PendingKyc` + numéro attribué ; `ClientCreated` écrit dans l'outbox **avant** `SaveChangesAsync`, donc dans la même transaction. Un test relit la ligne et vérifie que pièce, téléphones, date de naissance et revenus sont chiffrés et leurs index aveugles renseignés. Âge sous le minimum ⇒ `CLIENT_UNDER_MINIMUM_AGE`. Agence hors périmètre ⇒ `AGENCY_OUT_OF_SCOPE` via `AgencyAuthorizationBehavior`. |
| BE-05 Contrôle de doublons | **OK** | Pièce déjà présente ⇒ 409 `DUPLICATE_IDENTITY_DOCUMENT` avec le numéro client existant dans le corps. Téléphone rattaché à un client actif ⇒ 409 `POSSIBLE_DUPLICATE_PHONE` avec les candidats (numéro, nom affiché, agence). `ConfirmNoDuplicate = true` fait aboutir la création, et la confirmation est dans le payload audité. **Aucun déchiffrement** pendant le contrôle : un faux `IFieldEncryptor` compteur l'atteste. |
| BE-07 Modification non sensible | **OK** | `customers:update`, audité par le pipeline. `xmin` divergent ⇒ `CONCURRENCY_CONFLICT`. Client `Archived`/`Merged` ⇒ `CLIENT_READ_ONLY`. |
| BE-08 Modification sensible | **OK** | Sans `customers:update_sensitive` ⇒ 403. Motif obligatoire (10–500 car.) ⇒ `REASON_REQUIRED`. L'audit porte acteur, horodatage, motif et la liste des champs **par nom**, jamais de valeur. `ClientSensitiveDataChanged` publié avec la seule liste des noms. Nouveau numéro de pièce ⇒ contrôle bloquant de BE-05. |
| BE-09 Coordonnées historisées | **OK** | Ajout chiffré, indexé, daté (`ValidFrom`) ; retrait = `ValidTo`, jamais de suppression physique ; un seul principal actif par type ; dernier téléphone actif ⇒ `LAST_PHONE_REQUIRED`. Ajout idempotent sur (type, index aveugle). |
| BE-10 Consulter la fiche | **OK** | Champs sensibles **masqués** (`CI•••••••42`, `+225 07 •• •• 18`). Hors périmètre **ou** inexistant ⇒ 404, jamais 403 : le périmètre ne révèle pas l'existence d'un dossier. Client `Merged` ⇒ identifiant et numéro du survivant. |
| BE-11 Révélation auditée | **OK** | Seule la valeur du champ demandé est déchiffrée. Entrée `sensitive_data_access_logs` (acteur, client, nom du champ, horodatage). `Cache-Control: no-store` + `Pragma: no-cache`, posés avant dispatch donc présents aussi sur 404 et 429. Au-delà de `reveal-limit-per-hour` ⇒ 429 + `SensitiveRevealThresholdExceededEvent`, publié dans un `TransactionScope(Suppress)` pour que le refus n'annule pas l'alerte. |
| BE-12 Recherche | **Réserve** | Téléphone et pièce : **égalité sur index aveugle** après normalisation. Nom : préfixe ≥ 3 caractères, insensible aux accents et à la casse, sur `Client.SearchKey` normalisée par `SearchKeyBuilder`, utilisée à l'écriture **et** à la lecture. Filtres cumulatifs, pagination, périmètre d'agence appliqué. **Non vérifié** : le p95 < 300 ms à 100 000 clients n'a pas été mesuré, faute de PostgreSQL dans cet environnement. Les index `(TenantId, SearchKey)`, `ux_clients_identity_doc` et `(TenantId, Type, BlindIndex)` sont en place pour le soutenir. |

### Epic 3 — Cycle de vie et KYC

| US | État | Détail |
|---|---|---|
| BE-13 Statut piloté par le KYC | **OK** | `PendingKyc` + `KycValidated` ⇒ `Active` + `ClientActivated`. `KycRejected` ⇒ statut + motif. `KycRiskLevelChanged` ⇒ instantané seul. Client `Suspended`/`Archived` ⇒ **seul `KycStatus`** change : une décision tardive ne ressuscite pas un dossier archivé. Rejeu ignoré via `inbox_messages`. Acteur SYSTEM (`BackgroundJobContext.SetScope(tenantId, Guid.Empty, "SYSTEM")`). Stub de développement actif seulement si `IsDevelopment()` **et** `kyc-stub-enabled` — un test vérifie qu'il ne laisse aucune trace hors développement. Les trois événements KYC ont été ajoutés au PublicApi de M02. |
| BE-14 Suspendre / réactiver | **OK** | Motif obligatoire, `ClientSuspended` publié. Réactivation ⇒ `Active` si KYC validé, sinon `PendingKyc`. Chaque transition ajoute une ligne `client_status_history` (ancien, nouveau, motif, acteur, date). Transition interdite ⇒ `INVALID_STATUS_TRANSITION`. |
| BE-15 Archiver | **Réserve** | `Archived` + `ArchivedAt` + `ClientArchived`. Mutation ultérieure ⇒ `CLIENT_READ_ONLY`. **Aucun endpoint ne supprime physiquement un client** (vérifié sur les 54 routes). Le critère « encours actifs » est, dans l'US même, conditionné à l'arrivée de M03/M04 : il est implémenté comme point d'extension `IOutstandingBalanceProbe`, dont l'implémentation par défaut répond « aucun encours ». Le code appelant n'aura pas à changer. |
| BE-16 Conseiller / agence | **OK** | Conseiller inactif ou hors de l'agence du client ⇒ `ADVISOR_NOT_ELIGIBLE` (via `IAdministrationModule`). Transfert hors périmètre ⇒ refusé par le behavior. `ClientTransferred` publié et conseiller réinitialisé si l'ancien n'appartient pas à l'agence cible. |

### Epic 4 — Personnes morales

| US | État | Détail |
|---|---|---|
| BE-17 Créer un client PM | **OK** | `PendingKyc` + `ClientCreated`. RCCM déjà présent ⇒ 409 `DUPLICATE_REGISTRATION_NUMBER`. Forme juridique issue de `legal_forms`, liste fermée paramétrable par tenant (9 formes OHADA semées) ⇒ sinon `LEGAL_FORM_UNKNOWN`. RCCM et NIF chiffrés, index aveugle sur le RCCM seul — le NIF n'est jamais recherché, un index de plus serait un oracle d'égalité gratuit. |
| BE-18 Bénéficiaires effectifs | **OK** | Bénéficiaire soit client lié, soit personne externe à identité minimale avec pièce chiffrée. Total > 100 % ⇒ `OWNERSHIP_EXCEEDS_100`. Si personne n'atteint `beneficial-owner-threshold` (25 % par défaut) ⇒ un `ControlType.Manager` est exigé, sinon `MANAGER_BENEFICIAL_OWNER_REQUIRED`. Déclaration ensembliste : l'ancien bénéficiaire est clôturé par `ValidTo`, jamais supprimé. `BeneficialOwnersChanged` publié. |

### Epic 5 — Groupes

| US | État | Détail |
|---|---|---|
| BE-19 Créer un groupe | **OK** | Statut `Forming`, `GroupCreated` publié. Nom déjà utilisé dans la même agence ⇒ `GROUP_NAME_ALREADY_USED`, détecté en base **et** par interception de la violation d'unicité 23505 (course). |
| BE-20 Membres et rôles | **Réserve** | Client `KycRejected`/`Archived`/`Merged` ⇒ `GROUP_MEMBER_NOT_ELIGIBLE`. Taille max par type ⇒ `GROUP_SIZE_LIMIT_REACHED`. Retrait = `LeftAt` + motif, jamais de suppression. Rôle de bureau unique, l'ancien titulaire redevient `Member`. `GroupMembershipChanged` publié. **Réserve assumée, conforme à l'US** : la règle « un seul groupe de caution solidaire » est implémentée mais pilotée par le paramètre `solidarity-single-group-rule` — l'US la marque « à confirmer avec le métier », le paramètre permet de l'activer ou la désactiver sans livraison. |
| BE-21 Cycle de vie du groupe | **OK** | Passage automatique `Forming` → `Active` seulement si taille minimale **et** les trois rôles pourvus (3 tests négatifs sur les cas partiels). Descente sous le minimum ⇒ `ClientUnderMinimumGroupSize` publié **sans** changement de statut. Dissolution ⇒ `Dissolved` + clôture de toutes les adhésions actives + `GroupDissolved`. |

### Epic 6 — Relations

| US | État | Détail |
|---|---|---|
| BE-22 Relations d'un client | **OK** | Cible client ou personne externe. `Conjoint` entre deux clients ⇒ réciproque créée et liée par `ReciprocalRelationshipId` ; clôturer l'une clôture l'autre. Relation vers soi-même ⇒ `SELF_RELATIONSHIP_FORBIDDEN`. Clôture datée et conservée. `GuarantorLinked` publié pour M04. `DependentsCount` **recalculé** depuis les relations actives, jamais incrémenté. |

### Epic 7 — Doublons et fusion

| US | État | Détail |
|---|---|---|
| BE-23 Clé phonétique | **Réserve** | Normalisation (accents, casse, variantes `ou/w`, `dj/j`, `kh/k`, lettres doublées) puis Double Metaphone. Les paires du jeu de test obtiennent la même clé : Ouattara/Wattara, Diallo/Jallo, Kouassi/Kwasi, Traoré/Traore, Koné/Kone, Yaou/Yao, Bamba/Banba, Cheikh/Chek. Clé recalculée à toute création ou modification de nom. Job de rattrapage `BackfillPhoneticKeysJob` par lots de 500. **À valider par le métier** : le repliement a été étendu à `DI`+voyelle → `J` et `OU`+voyelle → `W` en toute position, ce qui rapproche aussi Ndiaye/N'Diaye et Ouedraogo/Wedraogo — le rappel augmente, au prix de faux positifs arbitrés par le seuil. |
| BE-24 Détection planifiée | **OK** | Orchestrateur global `0 2 * * *` qui enfile un job par tenant, arguments = identifiants opaques, identité SYSTEM, commande `ICommand` donc auditée au compte système. Score sur clé phonétique, index aveugle de la date de naissance, agence et noms des parents. **Blocage obligatoire** par clé phonétique, index de date de naissance et index de pièce : jamais de produit cartésien, avec log d'alerte au-delà de 1 000 éléments par bloc. Paire déjà `ToReview` rafraîchie ; paire `Rejected` non reproposée tant que les empreintes des deux fiches sont inchangées, reproposée si l'une bouge ; paire `Merged` ignorée. Un test échoue si un futur refactor introduit un déchiffrement. |
| BE-25 Fusion à quatre yeux | **Réserve** | Demande avec choix champ par champ, `WorkflowInstance` M12 créée. Auto-approbation refusée ⇒ `SELF_APPROVAL_FORBIDDEN` (sur l'approbation **et** le rejet). Seconde décision ⇒ `MERGE_ALREADY_DECIDED`. À l'approbation : coordonnées, relations, adhésions, bénéficiaires et timeline rattachés au survivant, l'absorbée passe `Merged` avec `MergedIntoId`, `ClientsMerged` publié, et `ResolveClientId` renvoie le survivant (testé sur une chaîne à 3 niveaux). KYC divergents avec au moins un `Approved` ⇒ `ClientSensitiveDataChanged(["KycStatus"])` pour rouvrir la revue. **Écart** : la garantie des quatre yeux est portée par M01, pas par le moteur M12 — celui-ci n'implémente aucun contrôle d'auto-approbation (vérifié dans son domaine et ses tests). L'instance M12 sert la traçabilité ; son échec ou l'absence de template ne fait pas échouer la demande. Aucun consumer de complétion : `WorkflowCompletedIntegrationEvent` vit dans l'assembly principale de M12, non référençable sans violer l'isolation des modules. |

### Epic 8 — Vue 360°

| US | État | Détail |
|---|---|---|
| BE-26 Timeline | **OK** | Entrée = module source, type, date, résumé et référence de consultation. Rejeu sans doublon : double garde `inbox_messages` + index unique `(TenantId, DedupKey)`. Le résumé est **réellement** expurgé, pas par convention : `TimelineSummaryGuard` retire e-mails, dates et suites de ≥ 7 chiffres, en préservant les GUID et les numéros client. Historique commercial du lead importé sur `ClientCreated` porteur d'un `SourceLeadId`, via `ILeadsModule.GetLeadHistoryAsync` ajouté au contrat de M13. L'arrivée de M02/M03/M04/M08 n'ajoutera que des consumers : le modèle ne change pas. |
| BE-27 Segments (V1.1) | **OK** | Règles par tenant en JSON, évaluées par priorité croissante, premier match gagnant. Changement historisé + `ClientSegmentChanged` publié. Les règles reposant sur des données M03/M04 sont **ignorées et signalées inactives** (`IsEvaluable = false` dans `GET clients/segments/rules`, plus un log par exécution). Aucun match laisse le segment en place plutôt que de le vider, pour ne pas faire sortir un client de toutes les campagnes. |
| BE-28 Score de fidélité (V1.1) | **Réserve** | Score 0–100 historisé avec `BreakdownJson`, marqué provisoire si le client a moins de 90 jours. **Écart assumé** : `volume` et `products` dépendent de M03/M04 ; ils sont présents dans le breakdown avec `isAvailable = false` et le score est normalisé sur les pondérations disponibles — sinon les pondérations d'usine plafonneraient un client parfait à 60/100. |

### Epic 9 — Conformité

| US | État | Détail |
|---|---|---|
| BE-29 Fin de conservation (Backlog) | **Réserve** | Job mensuel `0 4 1 * *` listant les clients archivés depuis plus de `retention-years` (plancher de 10 ans imposé **en lecture comme en écriture**, pour qu'une modification directe en SQL ne puisse pas raccourcir le délai légal). `IKycModule.IsRetentionClearedAsync` non confirmé ⇒ le client est **exclu** de la proposition. Anonymisation attribuée au compte SYSTEM. **Écart** : l'US demande de « réutiliser le mécanisme d'anonymisation de M13 » — ce mécanisme n'existe pas (aucune occurrence dans M13). M01 implémente le sien : noms remplacés, champs chiffrés et index aveugles vidés, contacts clôturés, `ClientAnonymized` publié. |
| BE-30 Export (V1.1) | **Réserve** | Génération en tâche de fond, lien à durée limitée (`export-link-ttl-minutes`), comparaison du token en temps constant, `EXPORT_LINK_EXPIRED` / `EXPORT_NOT_FOUND`, `Cache-Control: no-store`. Champs sensibles **masqués** dans le CSV. Export audité avec les filtres, nombre de lignes journalisé à la complétion. Le job rejoue la recherche sous SYSTEM mais en réappliquant le **périmètre du demandeur**, jamais celui du compte système. **Écart mineur** : le token est de 24 octets en hexadécimal (192 bits) au lieu de 32 octets en base64url — la propriété de sécurité tient, l'encodage diffère du contrat. |

## 3. Definition of Done — transverse

| Exigence | État |
|---|---|
| Policy RBAC explicite par endpoint, jamais `[Authorize]` seul | **OK** — 54/54 endpoints ; 0 occurrence de `[Authorize]` |
| Filtrage par périmètre d'agence (behavior + `PermissionAttribution`) | **OK** — `AgencyAuthorizationBehavior` créé (il n'existait pas), `AgencyScopeProvider` parcourt la hiérarchie d'agences **et** les `PermissionAttribution` fenêtrées |
| Mutation tracée dans l'audit via le pipeline | **OK** — toutes les commandes sont `ICommand` + `IResourceCommand` |
| Champs chiffrés journalisés par nom, jamais par valeur | **OK** — `[SensitiveData]` sur chaque champ sensible ; le sérialiseur a dû être corrigé (§4) |
| Test d'isolation multi-tenant par zone | **OK** — présent dans chaque zone, y compris via index aveugle |
| Documentation OpenAPI générée | **OK** — `.WithOpenApi()` + `Produces<...>` + description sur chaque endpoint |
| Tests d'intégration sur PostgreSQL réel | **Non couvert** — voir §5 |
| Aucun warning SonarQube critique ou élevé | **Non vérifié** — SonarQube n'est pas exécutable ici ; l'avis de sécurité NU1903 (Newtonsoft.Json 11.0.1, sévérité haute) a en revanche été **supprimé de tout le dépôt** |

## 4. Défauts de fond corrigés hors périmètre des zones

Trois défauts auraient rendu le module inutilisable en production et ont été corrigés à la source.

1. **L'audit faisait échouer toute création de client.** `SanitizedJsonSerializer` remplaçait le
   *getter* d'une propriété `[SensitiveData]` par `"***"` ; System.Text.Json recastant le résultat
   vers le type déclaré, une propriété `DateOnly?`, `IReadOnlyList<string>` ou un record imbriqué
   levait `InvalidCastException`. Le pipeline sérialisant **chaque** commande, `CreateIndividualClient`
   et `CreateLegalClient` auraient échoué systématiquement. Corrigé par un `JsonConverter` par
   propriété : le nom du champ survit, la valeur est rédigée, quel que soit le type. 4 tests de
   non-régression.
2. **Toute transition de statut échouait en base.** 17 configurations EF sur 18 laissaient la clé
   primaire en `ValueGenerated.OnAdd` alors que les factories assignent `Id = Guid.NewGuid()`. Une
   ligne d'historique, un point de contact ou une adhésion ajoutée à un agrégat déjà suivi était
   classée `Modified` au lieu de `Added`, et `SaveChangesAsync` levait *« Attempted to update or
   delete an entity that does not exist in the store »*. Corrigé par une convention unique sur tout
   le modèle dans `OnModelCreating` : une future entité ne peut plus l'oublier. Les jetons `xmin`
   ne sont pas touchés, ce ne sont pas des clés.
3. **Contournement `UnsafeAccessor` supprimé.** M13 persiste l'identifiant du client sur le lead
   *avant* d'appeler M01 ; les factories codant `Guid.NewGuid()` en dur, les deux enregistrements
   se décrochaient. Les factories acceptent désormais `Guid? id = null`.

4. **`TimeProvider` n'était enregistré nulle part dans le dépôt**, alors que **13 handlers et jobs
   de M01 en dépendent** : toute la zone Conformité, l'exécution de fusion, l'export et les deux
   jobs nocturnes auraient échoué à la résolution au démarrage. Aucun test unitaire de handler ne
   pouvait le voir — ils construisent tous le handler avec `new` en injectant une horloge factice.
   Détecté par les tests de composition (§6), corrigé par un `TryAddSingleton(TimeProvider.System)`
   dans `AddCustomersModule`, en `TryAdd` pour qu'un hôte ou un test puisse substituer son horloge.

Également corrigé : `ILeadsModule` n'avait **aucune implémentation ni enregistrement DI** dans le
dépôt — `LeadsModuleFacade` a été écrit et enregistré.

## 6. Tests de composition

`Infrastructure/CustomersModuleCompositionTests.cs` construit le conteneur réel à partir de
`AddCustomersModule`, avec des substituts pour les seules dépendances que l'hôte possède, puis
résout **chaque** `IRequestHandler<,>`, **chaque** `IConsumer<>` et **chaque** job Hangfire du
module. Aucune base n'est touchée : `AddDbContext` ne fait qu'enregistrer comment ouvrir une
connexion.

C'est le filet qui manquait : les 730 autres tests construisent les handlers avec `new` et
prouvent leur logique, mais ne disent rien de la capacité du conteneur à les produire. Avec 10
dossiers de tranches verticales enregistrant chacun ses services, un `AddScoped` oublié ne se
verrait qu'au premier appel en production. Les tests vérifient aussi que le contrat historique
`Customer360.ICustomerModule` est bien servi par `LegacyCustomerModuleAdapter` et non plus par le
stub, et `ValidateScopes` est activé pour attraper un singleton capturant un service scoped —
la façon classique dont un `DbContext` survit à sa requête et se met à renvoyer des données
d'un autre tenant.

## 5. Ce qui reste à faire

1. **Tests d'intégration sur PostgreSQL réel** (exigence de la DoD). Non réalisable ici : aucun
   package Testcontainers dans `Directory.Packages.props` et Docker indisponible sur la machine.
   Les tests actuels sont au niveau handler sur EF InMemory, qui est la convention du dépôt (aucun
   test d'intégration n'existe dans aucun module). Marche à suivre : ajouter
   `<PackageVersion Include="Testcontainers.PostgreSql" Version="4.1.0" />`, créer
   `src/Modules/Customers/Sankore.Modules.Customers.IntegrationTests` avec une fixture partagée
   démarrant le conteneur et appliquant `Database.MigrateAsync()`, puis rejouer en priorité les
   comportements que l'InMemory ne peut pas prouver : index uniques filtrés
   (`ux_clients_source_lead`, `ux_client_contact_points_primary`, `ux_group_memberships_active`),
   concurrence `xmin` réelle, et les query filters sous transaction.
2. **Mesurer le p95 de la recherche** à 100 000 clients (US-BE-12), sur PostgreSQL, avec `EXPLAIN`
   pour confirmer que `(TenantId, SearchKey)` est bien utilisé sur le `LIKE 'TERM%'`.
3. **Décisions métier à confirmer** : le repliement phonétique étendu (BE-23), la règle mono-groupe
   solidaire (BE-20, déjà paramétrable), et l'encodage du token d'export (BE-30).
4. **Build `Release` cassé — préexistant.** `dotnet build -c Release` échoue avec 6 erreurs
   d'analyseurs (`CA1716`, `CA1000`) toutes situées dans `Sankore.Shared.Kernel`. Identique pour le
   module `Leads`, donc antérieur à ce travail : `Directory.Build.props` active
   `TreatWarningsAsErrors` en Release alors que le dépôt ne compile qu'en Debug. À traiter au niveau
   du dépôt, par un `NoWarn` ciblé ou en renommant les namespaces concernés.
5. **Échec de test préexistant** dans `Workflow` : `ActivateTemplateHandler` appelle
   `ExecuteDeleteAsync`, non supporté par le provider EF InMemory.
