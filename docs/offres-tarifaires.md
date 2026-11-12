# Offres Essentiel, Pro et Entreprise dans le CRM

**Statut : plan rédigé le 2026-11-12. Rien n'est démarré. D1, D3, D4 et D5 tranchées le 2026-11-12 (recommandations retenues) ; D2 et D6 à D14 en attente.**

## 1. Le besoin

Le site Talisoft (`talisoft-landing`, page `/tarifs`, fichier `src/app/pricing/pricing.data.ts`) vend trois offres :

| | Essentiel | Pro | Entreprise |
|---|---|---|---|
| Prix HT par utilisateur et par mois | 15 000 FCFA (12 500 à l'année) | 27 500 FCFA (23 000) | 45 000 FCFA (37 500), ou sur devis |
| Utilisateurs | sans minimum | sans minimum | 5 minimum facturés |
| Agences | 1 | jusqu'à 10 | illimitées |
| Mise en service | incluse | 500 000 FCFA | sur devis |
| Modules métier | non | non | Assurance, Banking - Microfinance |
| Essai | 30 jours gratuits | — | — |

Le comparatif du site liste une soixantaine de fonctions, dont une trentaine diffèrent d'une offre à l'autre. Le CRM doit appliquer ce que le site promet. Une institution Essentiel ne doit pas pouvoir activer ce qui est vendu en Pro, ni une institution Pro ce qui est vendu en Entreprise.

## 2. Ce qui existe aujourd'hui

- **Aucune notion d'offre dans le CRM.** Le registre (`Sankore.Admin`) a `Tenant.Offer` (`Trial` / `Paid`) et `TrialExpiresAt`. `TenantResponse` les envoie, mais `TenantInfo` (Kernel) ne lit pas `Offer`. L'API ne s'en sert donc jamais. Aucune tâche n'applique la fin d'essai.
- **Le seul verrou commercial est celui des modules**, à reprendre tel quel pour les offres :
  - `TenantModules` / `ITenantModules` (Kernel) ;
  - le filtre `RequireTenantModule` (`Shared.Infrastructure/Tenants/TenantModuleEndpointFilter.cs`) : 403 `TENANT_MODULE_DISABLED`, mais GET et HEAD passent quand le module est désactivé (consultation seule) ;
  - `ModuleScopes`, qui filtre les rôles et les droits ;
  - `CurrentUserDto.ActiveModules` / `ReadOnlyModules` ;
  - côté front, `TenantModulesService`, `hasModuleGuard` et le champ `modules` du catalogue des paramètres.
- **Quelques limites existent déjà, réglées par l'institution elle-même et non par la plateforme :**
  - quota mensuel d'e-mails et de SMS : `TenantNotificationSettings.MonthlyQuotaLimit` et `TenantSmsSettings`, appliqué par `EmailOutboxProcessor` / `SmsOutboxProcessor` ; vide = illimité ;
  - 20 webhooks sortants au plus par institution (`WebhookSubscriptionsFeature.MaxPerTenant`) ;
  - un seul circuit actif par (entité, circuit).
- **Pas de comptage des utilisateurs facturables.** Le seul compte est `GET users/stats/status`, par statut. Le mode test partage les utilisateurs de la production (`TeamTenantId`). Il ne faut donc jamais compter deux fois.

## 3. Le principe proposé

**Une offre par institution, tenue par le registre et choisie par la plateforme, lue par l'API comme les modules.**

1. **Registre.**
   - `Tenant.Plan` (`Essentiel`, `Pro`, `Entreprise`) et `Tenant.PlanOptions` (pour l'instant : `BiometricKyc`, l'option vendue avec Pro, et un quota d'e-mails sur mesure).
   - Chaque changement est historisé (qui, quand, d'où vers où).
   - `TenantProjection` les sert dans `TenantResponse`. Le jumeau du mode test prend l'offre de sa production, comme pour les modules.
2. **Un seul catalogue, dans le Kernel : `PlanFeatures`.**
   - Pour chaque fonction : un code stable et l'offre minimale qui l'ouvre.
   - Pour chaque limite : sa valeur par offre (sources connectées, agences, circuits actifs, durée d'audit lisible, quota d'e-mails).
   - Le § 5 de ce document est sa transcription. Un test épingle le catalogue contre ce tableau. Le site et le CRM ne peuvent pas partager un fichier (deux dépôts), d'où ce test plutôt qu'un fichier commun.
3. **Lecture : `ITenantPlan`** (sur `ITenantStore` avec cache, comme `ITenantModules`).
   - `AllowsAsync(feature)` et `LimitAsync(limit)`.
   - En cas de panne du registre, lecture « ouverte », comme les modules : une institution payante ne doit pas perdre ses écrans parce que le registre ne répond pas pendant une minute.
4. **Serveur.**
   - Filtre `RequirePlanFeature(code)` sur les groupes de routes. Réponse 403 `PLAN_FEATURE_UNAVAILABLE` avec l'offre requise.
   - GET et HEAD passent toujours, pour que les données créées sous une offre supérieure restent lisibles (D3).
   - Les limites chiffrées se vérifient dans les handlers de création ou d'activation : 409 `PLAN_LIMIT_REACHED`, avec la limite et le compte actuel.
   - Les tâches de fond d'une fonction retirée vérifient l'offre avant de travailler (D3).
5. **Front.**
   - `CurrentUserDto.Plan`, `PlanFeatures` et `PlanLimits` ; `TenantPlanService` (`can(feature)`, `limit(name)`) ; `hasPlanFeatureGuard`.
   - Champ `plan` sur les entrées du menu et du catalogue des paramètres.
   - Une page « Disponible avec l'offre Pro » et un message unique de l'intercepteur d'erreurs pour les deux codes.
6. **Plateforme.**
   - L'app admin choisit l'offre (fiche institution et tiroir « Nouveau tenant ») et les options.
   - Elle refuse d'autoriser un module hors Entreprise, et de descendre sous Entreprise tant qu'un module est actif.
   - L'approbation d'un essai pose l'offre de l'essai (D1).

Ce qui ne change pas : les modules restent un second axe (« Entreprise » ne les active pas : il permet de les demander), et `ModuleScopes` reste le filtre des rôles et des droits métier.

## 4. Décisions à prendre

| # | Sujet | Recommandation |
|---|---|---|
| D1 | Offre de l'essai gratuit | **Décidé le 2026-11-12.** **Essentiel**, comme l'annonce le site (badge « 30 jours gratuits » sur la seule carte Essentiel). Avantage : la consigne « pas d'export pendant l'essai » (2026-11-11) est tenue d'office, l'export étant Pro. Alternative : un essai en Pro, plus démonstratif, mais le site serait à reprendre. |
| D2 | Institutions existantes | **Entreprise** pour toutes à la migration, pour que rien ne change. La plateforme descend ensuite celles qui doivent l'être. Les essais en cours prennent l'offre de D1. |
| D3 | Passage à une offre inférieure | **Décidé le 2026-11-12.** Sur le modèle des modules. **Les données restent lisibles. Créer et activer est refusé.** **Les automatismes d'une fonction retirée s'arrêtent** : nurturing, bot WhatsApp, règles de dispatching, circuits au-delà de la limite. Sinon, descendre d'offre ne changerait rien au travail fait par le CRM. **Au-delà d'une limite chiffrée, l'existant reste et rien de nouveau n'est accepté.** C'est la différence avec les modules, dont les traitements de fond continuent après désactivation (décision du 2026-10-23). |
| D4 | Ce que voit une institution d'une fonction non incluse | **Décidé le 2026-11-12.** **Menus et paramètres** : l'entrée reste visible avec un badge « Pro » ou « Entreprise » et mène à une page qui explique l'offre (incitation à monter en gamme). **Boutons à l'intérieur d'un écran** : masqués. |
| D5 | Répartition des leads en Essentiel (« Règles de dispatching automatiques » = Pro) | **Décidé le 2026-11-12.** **Pas de dispatching à la capture.** Un lead reste à son créateur, ou en attente d'attribution manuelle (bouton « Attribuer »). L'écran des règles est en consultation seule. Avec 1 agence et une petite équipe, c'est le fonctionnement attendu. |
| D6 | Score et température en Essentiel (« Scoring, température » = Pro) | **Le score continue d'être calculé et affiché**, car il sert la page « Ma journée » et l'ordre des tâches. **Seuls les réglages sont fermés** : seuils, SLA par température, refroidissement, grands comptes. Masquer la température partout coûte cher côté front et n'apporte rien. |
| D7 | KYC en Essentiel (« KYC simplifié et plafonds » ; « Validation des pièces et invitation à distance » = Pro ; « KYC complet » = Pro) | **Essentiel** : palier Simplifié seulement (un produit ne peut pas exiger le palier complet), pas de lien de collecte à distance, et les pièces sont acceptées avec la validation du dossier, sans revue pièce par pièce. **Pro** : tout le KYC sans la biométrie, sauf option D13. **Entreprise** : tout, y compris l'auto-approbation et les données PPE. À confirmer : est-ce bien ce que veut dire « Validation des pièces » ? |
| D8 | Quota d'e-mails (« Quota de base », « Quota ×5 », « Sur mesure ») | Valeurs à fixer. Proposition : **2 000 par mois en Essentiel, 10 000 en Pro, au contrat en Entreprise.** Le quota ne porte que sur le **relais de la plateforme**. Une institution qui branche son propre SMTP ou son compte Brevo n'est pas limitée. Le réglage actuel du quota par l'institution devient un plafond interne, qui ne peut pas dépasser celui de l'offre. Le D6 de l'essai (500 par mois) devient le quota de l'offre de l'essai, ou reste spécifique à l'essai : à trancher. |
| D9 | Ce qui compte comme « source connectée » et comme « agence » | **Sources** : seuls les canaux connectés (formulaire web, webhook, import programmé, Lead Ads), pas les sources internes créées au démarrage (AGENCE, PARRAINAGE, APPEL-ENTRANT, IMPORT-FICHIER). Une source désactivée ne compte plus. **Agences** : agences actives hors siège. Le démarrage crée « Siège » et « Agence principale » : sinon une institution Essentiel neuve serait déjà au-delà de sa limite. |
| D10 | Circuits comptés dans « 3 workflows actifs » | **Les circuits dessinés par l'institution seulement.** Le circuit de validation KYC semé à la création, et celui du dossier de crédit (module), ne comptent pas. Sans cette exclusion, Essentiel (aucun workflow) perdrait la validation KYC. |
| D11 | « Journal d'audit 90 jours » en Pro | **Une fenêtre de lecture, pas un effacement.** Le journal est une preuve de conformité et n'est jamais purgé (`TechnicalRetention`). En Pro, l'écran et l'API ne montrent que les 90 derniers jours. En Essentiel, l'écran est fermé. Repasser en Entreprise rend tout visible. |
| D12 | Utilisateur facturable et facturation | **Utilisateur facturable** : compte Actif ou en attente d'activation. Ne comptent pas : le compte système, les comptes désactivés et ceux de l'équipe Talisoft (support). **Relevé mensuel** : le CRM relève chaque jour le nombre, le registre garde le maximum du mois par institution. L'app admin l'affiche avec le montant estimé (prix de l'offre × max(utilisateurs, minimum)). **Facturation** : la facture elle-même reste hors du CRM dans ce plan. |
| D13 | Option « KYC biométrique » en Pro | Une option posée par la plateforme sur la fiche de l'institution (`PlanOptions.BiometricKyc`). Elle est incluse d'office en Entreprise. Son prix est à fixer, et le site ne l'affiche pas encore. |
| D14 | Qui change l'offre | **La plateforme seule**, comme l'autorisation d'un module. L'institution voit son offre sur une page « Mon offre » et peut demander à monter en gamme (e-mail aux `PlatformRecipients`). |

## 5. Le catalogue : chaque ligne du comparatif et son verrou

Légende : E = Essentiel, P = Pro, X = Entreprise. « Toutes » = rien à verrouiller. Les chemins sont relatifs à `src/Modules/<Module>/Sankore.Modules.<Module>/` ou au front (`packages/crm/…`).

### Prospection

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| Leads, projets, tâches, Ma journée, page Tâches | Toutes | — | — |
| Types de tâches paramétrables | P | `leads/task-types` en écriture (`TaskTypesEndpoints`). Les types système restent | `settings/task-types` |
| Ma journée des responsables, objectifs par agent | P | `GET leads/me/team-scorecard`, `leads/agent-objectives` (`GetTeamScorecard`, `AgentObjectives`) | vue équipe de `tasks/dashboard`, onglet « Objectifs » de la fiche utilisateur |
| Import de leads et de clients | Toutes | — | — |
| Export | P | Toutes les routes d'export : `leads/export`, `leads/reports/*/export`, `clients/reports/export`, `clients/exports`, `lead-sources/{id}/duplicates.csv`, `calls/export.csv`, export d'enregistrement, exports de mappings et de relevés | boutons « Exporter » |
| Sources de leads connectées | E 1 · P 5 · X ∞ | Création et activation (`CreateLeadSourceHandler`, `ActivateLeadSourceHandler`, copie depuis le mode test), selon D9 | compteur sur `settings/lead-sources` |
| Rejeu des données reçues | X | `leads/ingestions/{id}/replay` et `/reprocess` | `ingestion-detail-drawer` |
| Règles de dispatching automatiques | P | `leads/dispatching-rules` en écriture, `LeadAutoDispatchConsumer` et `DispatchWaitingLeadsJob` selon D5 | `leads/dispatching` |
| Température et SLA par température | P | `lead-settings/intent-thresholds` en écriture, champs `FirstContactSla*` d'une règle, refroidissement selon D6 | `intent-thresholds-card` |
| Grands comptes | P | `lead-settings/key-accounts`, `KeyAccountsOnly` et `ReservedAgentIds` d'une règle, dimension « Potentiel » du score | `key-accounts-card` |
| Nurturing et recyclage | P | `nurturing-sequences`, `leads/{id}/nurture`, `leads/{id}/recycle`, actions groupées ; `ExecuteNurturingJob` et `ReactivateRecycledLeadsJob` s'arrêtent (D3) | `settings/nurturing-sequences`, `nurture-recycle-drawer` |
| Rapports | E tableau de bord · P et X complets | `leads/reports/*`, `clients/reports`, `kyc-files/reports`. Le tableau de bord (`leads/stats`, `funnel`, `pipeline`) reste ouvert | entrée « Rapports » |

### Projets et devis

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| Projets, parcours standard, devis en lignes | Toutes | — | — |
| Parcours par produit (étapes, montant et date exigés) | P | Création et modification des gabarits (`ProjectTemplatesEndpoints`). Essentiel garde le parcours intégré, non modifiable | `settings/project-steps` |
| Prévision et échéancier | P | `TracksForecast` d'un gabarit et bloc « Prévision » du rapport (déjà fermé avec les rapports) | — |
| Plafond de remise et validation | P | `MaxDiscountPercent` de `company-info/pricing`. En Essentiel il est lu comme vide, donc aucune offre « À valider » | `pricing-rules-card` |
| Envoi des documents du projet | E e-mail · P et X + WhatsApp | `opportunities/{id}/whatsapp/*` (suit le verrou WhatsApp) | `project-whatsapp-drawer` |

### Clients

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| Réclamations, détection des doublons, catalogue | Toutes | — | — |
| Fusion des doublons | P | `clients/merges` en écriture | `merge-compare` |
| Formulaires dynamiques | P | `PUT forms/{entity}`. `GET` reste ouvert (le rendu des formulaires en a besoin) | `settings/forms` |

### Communication

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| Téléphonie, agendas, messagerie connectée | Toutes | — | — |
| E-mails et modèles (quota) | selon D8 | Plafond lu par `TryConsumeEmailQuotaAsync` : min(offre, réglage de l'institution), relais de la plateforme seulement | `notification-parametrage` affiche le plafond de l'offre |
| Canal WhatsApp, consentement, bot de qualification | P | `integration/messaging/channels` (création et activation), écritures de `leads/whatsapp/*`, `leads/whatsapp/clients/*`, `leads/whatsapp/bot` ; le bot se tait (D3) | `/leads/whatsapp`, `settings/whatsapp*` |
| Bot : création du lead et réclamations | P | Réglages `whatsapp-create-lead-*` et `whatsapp-bot-complaints-enabled` | `whatsapp-bot-page` |

### Conformité KYC (selon D7)

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| KYC simplifié et plafonds, effacement à deux | Toutes | — | — |
| KYC activable par l'institution | Toutes | — | — |
| Validation des pièces et invitation à distance | P | `kyc-files/{id}/documents/{docId}/review`, `RemoteInvitationsEndpoints`, `KycRequestedConsumer` n'émet plus de lien ; palier complet refusé sur un produit en Essentiel (`kyc-remote-policy`, palier du produit) | `kyc-file-detail` |
| KYC biométrique | P option · X | `kyc-files/{id}/verify`, `kyc-settings/biometry-token` | — |
| Auto-approbation | X | `KycAutoApprovalEnabled` lu comme faux hors Entreprise (`KycAutoApprovalPolicy`) | `auto-approval-controls` |
| Données PPE | X | `clients/{id}/household/pep`, `leads/{id}/pep` en écriture, revue PPE | blocs PPE |
| Alertes LBC/FT | P | `clients/{id}/suspicion-alerts`, `suspicion-alerts` | `raise-suspicion-alert-drawer`, `settings/alertes-lbc` |

### Automatisation

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| Workflows | E 0 · P 3 actifs · X ∞ | `ActivateTemplateHandler` compte les circuits actifs selon D10 ; copie depuis le mode test comprise | `settings/workflows`, `journey-builder` |
| Santé et analyses des workflows | X | `workflow/analytics/*` | `workflow-analytics`, santé des workflows |
| Webhooks sortants | P (20) · X (20) | `SaveWebhookSubscriptionHandler` : fermé en Essentiel ; `MaxPerTenant` inchangé | `settings/webhooks` |
| Événements de partenaires | X | `POST /api/ingest/hooks/{publicKey}/events` (`ExternalEventIngest`) répond 403 ; déclencheurs `ExternalEvent` refusés à l'ajout | éditeur de déclencheurs |

### Administration et sécurité

| Ligne du site | Offre | Verrou serveur | Front |
|---|---|---|---|
| Agences | E 1 · P 10 · X ∞ | `MapCreateAgency` et réactivation, selon D9 | `settings/agencies` |
| Territoires | P | `territories` en écriture | `settings/territories` |
| Modules métier | X | Registre : autorisation d'un module refusée hors Entreprise | page `settings/modules` |
| Rôles personnalisés | P | `roles` : création, modification, droits | `settings/roles` |
| Lecture limitée, données de départ | Toutes | — | — |
| Mode test | X | `POST auth/test-mode` (entrée), `auth/test-mode/refresh`, copies vers la production | bascule de l'en-tête, `settings/test-mode` |
| Journal d'audit | E fermé · P 90 j · X tout | `GET audit/entries` : fermé ou borné à 90 jours (D11) | `settings/audit` |
| Connexion en tant qu'un autre utilisateur | X | `users/{id}/impersonate` | `impersonate-dialog` |

### Accompagnement

| Ligne du site | Offre | Mise en œuvre |
|---|---|---|
| Support e-mail / téléphone / interlocuteur dédié | E / P / X | Hors code. La file des tickets de l'app admin affiche l'offre de l'institution |
| Demandes de support depuis le CRM | Toutes | — |
| Délai de prise en charge garanti | X | La file des tickets de l'app admin affiche l'échéance des tickets Entreprise (délai à fixer) |

## 6. Les lots

### Lot 1 — L'offre dans le registre et l'app plateforme (2 à 2,5 j)

- `Sankore.Admin` :
  - `TenantPlan` (enum), `Tenant.Plan`, `Tenant.PlanOptions` (jsonb) et `TenantPlanChange` (historique) ;
  - migration : toutes les institutions en Entreprise (D2), essais en cours selon D1 ;
  - `PUT tenants/{id}/plan` (clé de service plateforme) ;
  - `TenantProjection` : offre de la production pour un jumeau.
- Règles :
  - autoriser un module exige Entreprise ;
  - descendre sous Entreprise exige qu'aucun module ne soit actif ;
  - l'approbation d'un essai pose l'offre de D1 ;
  - `TenantProfiles` (tiroir « Nouveau tenant ») propose l'offre.
- App admin : section « Offre » de la fiche institution (offre, options, historique), colonne « Offre » de la liste, choix à la création.
- Tests du registre.

### Lot 2 — Le socle serveur (2 j)

- Kernel :
  - `TenantPlans`, `PlanFeature` (codes), `PlanLimit` et le catalogue `PlanFeatures` (transcription du § 5) ;
  - `TenantInfo.Plan` / `PlanOptions` ;
  - `ITenantPlan` et son implémentation sur `ITenantStore` ;
  - invalidation du cache au changement d'offre, comme pour les modules.
- Shared :
  - filtre `RequirePlanFeature(code)` (lectures permises) ;
  - `PlanGuard.EnsureWithinLimitAsync` pour les handlers ;
  - codes `PLAN_FEATURE_UNAVAILABLE` / `PLAN_LIMIT_REACHED`.
- `CurrentUserDto.Plan`, `PlanFeatures`, `PlanLimits`.
- Tests :
  - le catalogue est épinglé ligne à ligne contre le § 5 ;
  - un code inconnu est refusé ;
  - en cas de panne du registre, la lecture est « ouverte ».

### Lot 3 — Le socle front (1,5 j)

- `TenantPlanService` (`crm/common`), `hasPlanFeatureGuard`, champ `plan` du menu latéral et du catalogue des paramètres.
- Badge d'offre (D4) et page « Disponible avec l'offre … », qui propose « Demander à passer en Pro » (D14).
- Message de l'intercepteur pour les deux codes. Page « Mon offre » dans les paramètres : offre, limites et compteurs.
- Régénération du client d'API.

### Lot 4 — Prospection, projets, clients (3 j)

Verrous des tableaux « Prospection », « Projets et devis » et « Clients » du § 5, serveur et front, avec :

- l'arrêt des automatismes selon D3 (nurturing, recyclage, dispatching automatique selon D5) ;
- la limite des sources (D9).

Une ligne de test par verrou : Essentiel refusé, Pro accepté, lecture permise.

### Lot 5 — Communication, KYC, automatisation, administration (3 à 3,5 j)

Verrous des autres tableaux, dont :

- WhatsApp et le silence du bot ;
- le KYC selon D7 et D13 ;
- le comptage des circuits (D10) ;
- les agences (D9) ;
- le mode test ;
- l'usurpation ;
- les rôles ;
- le journal d'audit borné (D11) ;
- la fermeture des événements de partenaires.

### Lot 6 — Quota d'e-mails et relevé des utilisateurs (2 j)

- **Quota** : plafond d'offre (D8) dans `TryConsumeEmailQuotaAsync`, réglage de l'institution borné par lui, relais de la plateforme seulement.
- **Relevé quotidien des utilisateurs facturables** (D12), envoyé au registre par une tâche de l'API, sur le modèle de `TenantDataLoadJob`, avec la clé de service :
  - le registre garde le maximum du mois ;
  - l'app admin affiche « Utilisateurs facturés ce mois » et le montant estimé, minimum de 5 en Entreprise compris.
- Le mode test ne compte jamais (même équipe).

### Lot 7 — Changement d'offre et recette (1,5 à 2 j)

- Monter et descendre d'offre avec des données réelles :
  - limites dépassées gardées (D3) ;
  - automatismes arrêtés et repris ;
  - jumeau du mode test aligné.
- Recette sur trois institutions de test, une par offre, avec la démo chargée. Le jeu de démo est vérifié dans les limites de chaque offre (agences, sources, circuits). Sinon le démarrage d'une démo Essentiel échoue sur ses propres limites.
- Mise à jour de `CLAUDE.md` (règle : toute nouvelle fonction porte son code d'offre dès le départ, comme sa garde de module), de `deployment-dokploy.md` et de `essai-gratuit-socle.md` (D6 de l'essai).

**Total : 15 à 17 jours.**

## 7. Points d'attention relevés en préparant ce plan

- **Le démarrage d'une institution crée deux agences** (« Siège » et « Agence principale ») : à concilier avec « 1 agence » (D9).
- **La validation KYC repose sur un circuit de workflow semé par défaut** : à exclure du compte des workflows (D10).
- **Le quota d'e-mails est aujourd'hui réglé par l'institution** (`PATCH notification-settings/quota`). Sans plafond de l'offre, elle peut le lever elle-même.
- **Fin d'essai jamais appliquée** (`TrialExpiresAt` n'est lu par aucun contrôle) : c'est le lot 6 du plan d'essai, indépendant de celui-ci, mais à faire avant d'ouvrir les inscriptions.
- **Le site dit « facturation au 31e jour »** pour l'essai, alors que le parcours d'essai prévoit consultation seule, suspension puis effacement : à aligner (déjà signalé).
- **Option biométrique** : vendue en Pro mais absente des prix du site (D13).
