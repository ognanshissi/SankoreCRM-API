# Module Integration — plan d'exécution et traçabilité

38 user stories, deux familles de connecteurs (core banking + assurance) sur un socle commun.
Ce document est la référence que suit chaque tranche d'implémentation : il fixe les noms, les
signatures et les décisions, pour que plusieurs chantiers parallèles ne divergent pas.

Spécification source : les critères d'acceptation INT-01 → INT-34 et ASS-01 → ASS-12 fournis par
le propriétaire du produit. Ce document n'en invente rien ; il consigne les **écarts assumés** et
les **points laissés ouverts** par la spécification.

---

## 1. Écarts assumés par rapport à la lettre de la spécification

| Point | Spécification | Retenu | Raison |
|-------|---------------|--------|--------|
| Framework | « .NET 9 » (en-tête) | **net10.0** | Tout le dépôt est en `net10.0` (`Directory.Build.props`, `global.json`). Un module en net9 ne compilerait pas dans cette solution. |
| Codes de permission | `Integration.Connection.View` | **tels quels** | La spécification les tabule avec leurs rôles par défaut. Ils sont centralisés dans `IntegrationPermissions` : un éventuel passage à la convention du dépôt (`integration:connection:view`) est une édition d'un seul fichier. Voir §4. |
| `EntityType` | « l'enum extensible partagé » | **`string`, max 60** | Il n'existe aucun enum `EntityType` partagé dans le dépôt : la convention établie est une **chaîne** (`IContextProvider.EntityType`, `WorkflowStartRequest.EntityType`, `ClientTimelineEntry.ReferenceType`), chaque module déclarant sa constante. Une chaîne *est* le mécanisme d'extensibilité ici ; un enum central devrait être édité par chaque nouvelle famille. |

### Points laissés ouverts par la spécification, et comblés ici

| Sujet | Décision | Où |
|-------|----------|-----|
| Partitions futures de `integration_call_log` | Partitions mensuelles créées à l'avance par un job Hangfire (`EnsureCallLogPartitionsJob`), 3 mois d'avance. Ni `pg_cron` ni `pg_partman` ne sont installés dans ce dépôt. | INT-01 / INT-08 |
| PK d'une table partitionnée | `(id, at)` — PostgreSQL exige la clé de partitionnement dans toute contrainte d'unicité. | INT-01 |
| Ordre par client (INT-06) | `integration_command.created_at` + `id` en départage, avec un verrou optimiste sur `status`. L'index `(tenant_id, entity_type, crm_id, created_at)` de la spécification sert exactement cela. | INT-06 |
| Chiffrement du payload | Variante **keyed** (`IntegrationFieldProtection.Key`), jamais `AddFieldProtection(config, "Integration")` : le créneau non-keyed est déjà pris par M01 et un second appel lève une exception au démarrage. | INT-05 |
| Noms de champs dans l'audit | `[property: SensitiveData]` sur le payload + une propriété `PayloadFieldNames` non sensible calculée à la construction. `SanitizedJsonSerializer` remplace la valeur par `"***"` en conservant le nom. | INT-05 / INT-08 |
| Disjoncteur observable | Pipeline Polly explicite (`AddResilienceHandler` + `CircuitBreakerStateProvider` par connexion), car `AddStandardResilienceHandler` n'expose aucun état. | INT-09 |
| Files Hangfire | `AddHangfireServer` reçoit `Queues = ["default", "integration-write", "integration-sync", "integration-batch"]`. **`default` doit rester listé** ou tous les jobs existants cessent d'être traités. | INT-06 / INT-09 |
| Test d'architecture | Lecture des `ProjectReference` des `.csproj` (aucun NetArchTest dans le dépôt), ce qui teste la règle à sa source. | INT-02 / ASS-02 |
| SSRF | **Corrigé — ce raisonnement était faux.** J'avais écrit que `SsrfSafeHandler` n'était pas branché « parce qu'un CBS est fréquemment on-premise ». SANKORE est multi-tenant et hébergé : une adresse privée vue depuis ce processus est **son propre réseau**, jamais celui de l'IMF. Un système on-premise est inatteignable directement par construction — c'est précisément pourquoi `IntegrationMode` a trois valeurs et pourquoi INT-26 existe. `Relay` est la route vers une cible privée ; `Api` et `Batch` passent par l'internet public. Voir §5bis(e). | INT-09, INT-24 |
| Plafonds KYC (INT-22) | Lus via `IKycModule` / `kyc_settings` (`simplified-max-balance`, fenêtre de flux, pourcentage d'alerte **existent déjà** en M02). Aucune duplication. | INT-22 |
| `IKycModule.GetFlowUsageAsync` | Déjà déclaré en M02 et volontairement non implémenté « en attendant un module qui possède comptes et soldes ». Ce module le devient : la façade l'alimente depuis le snapshot. | INT-21 |
| Inbox de `KycValidatedEvent` | Le consommateur d'INT-14 dérive son propre identifiant d'inbox (SHA-256 de `messageId:consumerKey`). **Correction d'une justification erronée** : j'avais écrit que sans cela M01 et ce module se neutraliseraient sur la clé primaire de l'inbox. C'est faux — les tables sont distinctes (`customers.inbox_messages` et `integration.inbox_messages`), donc la collision inter-modules est physiquement impossible. Le mécanisme reste nécessaire, pour une raison plus faible : **deux consommateurs du même module** se partageant un événement, ce dont M01 a déjà eu besoin. | INT-14 |
| Rôles par défaut (INT-11) | « superviseur » et « contrôle interne » ne sont pas des rôles de cette plateforme. Lus comme `SalesManager` + `BranchManager` et `RegulationManager`. Table déclarative `RoleSeeder.DefaultGrants`, additive et idempotente : elle ne révoque jamais, donc un tenant qui a resserré un rôle à la main garde sa décision. | INT-11 |
| `RelayAgentId` est **server-set only** | Absent des contrats de requête de création et de mise à jour d'une connexion, et posé uniquement par le flux d'enrôlement d'INT-27. Même remède que `Lead.LeadSourceConfigId` dans ce dépôt, pour une raison plus grave : l'agent relais exécute des ordres *dans* le réseau de l'IMF (HTTP local, SFTP, SQL en lecture), donc un id librement choisi par le client ferait exécuter les payloads d'un tenant dans le réseau d'un autre, et lui donnerait accès à ses répertoires et ses vues. Fuite croisée dans les deux sens. Un test de non-régression interdit la réapparition du champ. | INT-03, INT-26, INT-27 |
| Chevauchement M12 | `POST products/{id}/link-cbs` (`BusinessProductId`, `BusinessPlatformName`) existe déjà en Administration. Le domaine `Product` d'`integration_mapping` le **remplace** fonctionnellement ; l'ancien endpoint reste, aucune migration de données n'est faite sans décision. | INT-04 |

---

## 2. Schéma `integration` — tables, telles que spécifiées

Toutes portent `tenant_id`, toutes sont couvertes par le filtre de requête tenant, aucune clé
étrangère physique vers un autre schéma. `connection_id` est une FK **intra-schéma** (autorisée).

| Table | PK | Unicité et index |
|-------|-----|------------------|
| `integration_connection` | `id` | `ux` partiel `(tenant_id)` WHERE `is_active AND family = 'CoreBanking'` |
| `integration_command` | `id` | `ux (tenant_id, idempotency_key)` · `ix (status, next_attempt_at)` · `ix (tenant_id, entity_type, crm_id, created_at)` |
| `integration_reference` | `id` | `ux (tenant_id, connection_id, entity_type, crm_id)` · `ux (tenant_id, connection_id, entity_type, external_id)` |
| `integration_batch_file` | `id` | `ux (tenant_id, connection_id, direction, sequence_no)` |
| `integration_sync_cursor` | `(tenant_id, connection_id, stream)` | — |
| `integration_mapping` | `id` | `ux (tenant_id, connection_id, domain, crm_code)` |
| `integration_reconciliation_run` | `id` | `ix (tenant_id, started_at)` |
| `integration_reconciliation_gap` | `id` | `ix (tenant_id, resolution)` |
| `integration_call_log` | `(id, at)` | **PARTITION BY RANGE (at)**, mensuel · `ix (tenant_id, at)` |
| `cbs_customer_snapshot` | `(tenant_id, crm_customer_id)` | — |

Seules les tables propres au core banking portent le préfixe `cbs_` (ASS-01).

## 3. Énumérations

```
IntegrationFamily   : CoreBanking, Insurance
IntegrationKind     : Temenos, Amplitude, Sab, PerfectVision, Orass, Fake
IntegrationMode     : Api, Batch, Relay
ErrorFamily         : Transient, Functional, Technical
CommandStatus       : Pending, Sending, Batched, RetryScheduled, Rejected, Succeeded, Cancelled
BatchDirection      : Out, In
MappingDomain       : IdDocType, Product, Agency, Country, Gender, MaritalStatus, Profession, Sector
GapType             : MissingInExternal, MissingInCrm, KycMismatch, StatusMismatch
SyncStream          : Customers, Accounts, Transactions, Loans, Policies, Claims
CapabilityMode      : RealTime, Batch
```

`ErrorFamily`, sémantique imposée par la spécification :

- **Transient** — timeout, 503, maintenance, relais injoignable → *retry*
- **Functional** — doublon, pièce refusée, produit inconnu → pas de retry, file de rejet
- **Technical** — mapping manquant, payload invalide, authentification refusée → pas de retry, alerte admin

## 4. Table de transitions des commandes (INT-05, littérale)

| Statut | Entrée | Sorties |
|--------|--------|---------|
| `Pending` | création, ou rejeu manuel d'une `Rejected` | `Sending`, `Cancelled` |
| `Sending` | prise en charge par un job (verrou optimiste sur `status`) | `Succeeded`, `RetryScheduled`, `Rejected`, `Batched` |
| `Batched` | ajoutée à un fichier sortant | `Succeeded`, `Rejected` |
| `RetryScheduled` | échec transitoire, `attempts < max` | `Sending` (à `next_attempt_at`) |
| `Rejected` | échec fonctionnel/technique, ou max atteint | `Pending`, `Cancelled` |
| `Succeeded` | succès confirmé | — final |
| `Cancelled` | annulation manuelle | — final |

Une transition hors table **lève une exception** (et non un `Result`) : la spécification l'exige.

## 5. Permissions (INT-11, ASS-11)

| Code | Usage | Rôles par défaut |
|------|-------|------------------|
| `Integration.Connection.View` | voir la connexion et son état de santé | Administrator |
| `Integration.Connection.Manage` | créer/modifier connexion, secrets, agent relais | Administrator |
| `Integration.Mapping.Manage` | gérer les correspondances | Administrator |
| `Integration.Command.View` | consulter commandes et file de rejet | Administrator, SalesManager |
| `Integration.Command.Replay` | rejouer ou annuler | Administrator |
| `Integration.Reconciliation.View` | consulter la réconciliation | Administrator, RegulationManager |
| `Integration.Reconciliation.Resolve` | marquer un écart résolu | Administrator |
| `CoreBanking.Balance.ViewLive` | appel direct de solde | Agent, SalesManager |
| `Ins.Product.Manage` `Ins.Policy.Subscribe` `Ins.Policy.View` `Ins.Claim.Declare` `Ins.Claim.View` `Ins.Statement.View` `Ins.Reconciliation.Resolve` | lot L8 | ASS-11 |

`RoleSeeder` n'accorde aujourd'hui **tout** qu'à `System` et `Administrator`, et rien aux autres
rôles. Les colonnes « rôles par défaut » autres qu'Administrator exigent donc une extension du
seeder — tracée comme tâche d'INT-11, pas comme acquis.

## 5 bis. Décisions de sécurité prises pendant l'implémentation

Six défauts relevés par la revue automatique, sur le socle puis sur les lots suivants. Pour
chacun, le correctif retenu est plus large que celui proposé, et pour une raison qui vaut d'être
écrite.

### a) `RelayAgentId` librement choisi par le client — INDOR inter-tenant

L'agent relais exécute des ordres *dans* le réseau de l'IMF : HTTP local, dépôts et lectures
SFTP, SQL en lecture. Un identifiant accepté dans un corps de requête permettait à un tenant de
router ses écritures par l'agent d'un autre, donc d'y faire exécuter ses payloads clients et d'y
lire répertoires et vues. **Retenu : le champ sort des contrats de requête** (même remède que
`Lead.LeadSourceConfigId`), le lien est posé par l'enrôlement d'INT-27. Le correctif proposé
validait contre une table qui n'existe pas encore.

### b) Payload corrigeable au rejeu — écriture financière non auditable

Un chantier avait ajouté `CorrectedPayloadJson` au rejeu : hors des critères d'INT-05. Avec
`DebitAccount` et `ReverseDebit` qui déplacent de l'argent (ASS-05), la permission
`Integration.Command.Replay` devenait « débiter n'importe quel compte, de n'importe quel
montant ». Et surtout, les deux exigences de sécurité se contredisaient : INT-08 interdit toute
valeur de payload dans l'audit, donc **une édition de payload est inauditable par
construction** — le seul contrôle qui l'aurait détectée est aveuglé par l'autre exigence.

**Retenu : la capacité est supprimée.** Le rejeu renvoie ce qui a été enregistré, ou re-dérive le
payload de l'état courant du CRM. Les pannes qu'elle prétendait traiter ont de meilleurs remèdes
déjà prévus : mapping manquant → l'ajouter (INT-04) ; identifiants refusés → corriger le secret
(INT-03) ; mauvaise valeur → corriger la fiche CRM, qui est la source de vérité. Un payload édité
à la main produirait d'ailleurs une écriture externe ne correspondant à rien dans le CRM, donc un
écart que la réconciliation d'INT-34 ne pourrait pas distinguer d'un vrai.

Si la correction de payload est voulue plus tard, c'est une US à part : permission propre,
quatre yeux, liste blanche de champs corrigeables par type de commande, et diff des noms de champs
dans l'audit.

### e) Sortie SFTP non validée — SSRF, et une décision de ma part à corriger

`SftpHost` et `SftpPort` viennent des settings de la connexion, qu'un administrateur tenant édite.
Les pointer vers `127.0.0.1`, `169.254.169.254` ou une adresse `10.x` faisait ouvrir à SANKORE une
session SFTP **depuis son propre réseau** et y déposer un fichier de données clients — ou lire un
service interne. L'identifiant est le nôtre, la position réseau est la nôtre ; seule la cible est
la leur.

**Ce signalement invalide une justification que j'avais écrite en §1.** « Un CBS est fréquemment
on-premise, donc on ne filtre pas RFC 1918 » est faux pour une connexion *directe* : dans un
déploiement hébergé multi-tenant, le privé c'est nous. La route vers une cible privée est `Relay`,
et c'est la raison d'être d'INT-26.

**Retenu** : résolution DNS puis validation de **chaque** adresse retournée avec la liste de
`SsrfSafeHandler` (loopback, RFC 1918, `169.254/16` où vivent les métadonnées cloud, CGNAT, ULA et
link-local IPv6), connexion à l'adresse **validée** et non au nom — sinon un rebind DNS passe entre
la vérification et le connect. Uniquement sur le chemin direct : le chemin relais n'ouvre aucune
socket ici. Échappatoire `Integration:Egress:AllowPrivateAddresses`, **défaut faux**, avec un
avertissement au démarrage quand elle est levée — et **jamais par tenant** : un tenant ne doit pas
pouvoir désactiver une protection de plateforme depuis ses propres paramètres, ce qui est la forme
même de cette vulnérabilité.

Les trois causes d'échec (injoignable, délai dépassé, clé d'hôte refusée) sont fondues en un seul
message côté tenant : trois échecs distinguables permettent de cartographier notre réseau interne
une adresse à la fois, ce qui est le vrai gain d'une SSRF une fois le connect bloqué.

**À faire côté Temenos** : le même raisonnement s'applique à `TemenosTransport` en mode `Api`, qui
ne valide rien aujourd'hui. Non corrigé dans ce lot — le chantier est clos et le changement touche
un chemin couvert par 116 tests. Tracé ici comme dette, pas comme oubli.

### d) Empreinte de certificat lue dans le corps de la requête — contournement d'authentification

Le heartbeat d'INT-27 est arrivé en route **anonyme** avec `CertificateThumbprint` dans le corps.
Une empreinte **n'est pas un secret** : c'est le hachage d'un certificat public, dérivable par
quiconque l'a vu une fois. N'importe qui atteignant la route pouvait donc publier un heartbeat
pour n'importe quel agent.

L'impact n'est pas évident et vaut d'être écrit : un heartbeat falsifiable fait **passer un relais
mort pour vivant**. Les fichiers batch cessent silencieusement d'être déposés, plus rien n'est
relayé dans le réseau de l'IMF, et le tableau de bord reste vert — strictement pire qu'un agent
visiblement tombé. Et si `IRelayAgentAdmission` accepte une empreinte venue d'un payload,
l'authentification de tout le canal s'effondre avec elle.

**Retenu** : l'empreinte est calculée côté serveur depuis le certificat client de la poignée de
main TLS (`GetClientCertificateAsync`, puis `GetCertHash(SHA256)` en hex minuscule, la forme que
`Admit` stocke), le champ disparaît du contrat de requête, et l'absence de certificat **refuse**
— même code unique que tous les autres refus relais, sans détail, parce qu'un appelant non
authentifié n'a pas à apprendre pourquoi.

Conséquence assumée : tant que les certificats clients ne sont pas activés sur la route (Kestrel
`ClientCertificateMode`, ou le reverse proxy qui transmet le certificat), **cet endpoint refuse
tout**. C'est le bon mode d'échec, et il fait partie de la même décision d'infrastructure que
l'ancrage PKI — que cette US ne peut pas trancher seule et dont il valait mieux nommer le manque
que fabriquer une autorité maison.

### c) `accountRef` non lié au client dans `GetLiveBalanceAsync`

INT-15 contrôle le périmètre d'agence sur le **client**. Un `accountRef` non vérifié laissait
passer un client de son agence plus n'importe quelle référence de compte du CBS. **Retenu :
vérification en deux temps dans la façade** — `integration_reference` d'abord, qui est
autoritatif parce que nous l'avons écrit (INT-07) et ne peut pas être périmé, puis la liste de
comptes du snapshot, qui couvre les comptes que nous n'avons pas ouverts (le portefeuille
historique est hors périmètre d'import). Si aucun des deux ne connaît le compte, aucun appel au
CBS et retour `null`. L'appel part avec l'identifiant **issu de nos propres données**, jamais la
chaîne de l'appelant. Placé dans la façade et non dans l'endpoint : c'est le point de passage de
tous les appelants, y compris le débit de prime d'ASS-05, qui prend aussi une référence de compte.

## 5 ter. Ce qu'INT-27 devra garantir

L'enrôlement d'un agent relais est le seul endroit où le lien connexion ↔ agent peut être posé
sans créer d'IDOR : c'est lui qui frappe le certificat, donc lui seul connaît le tenant de l'agent
au moment où il l'associe. Deux obligations en découlent, à tenir au moment d'écrire INT-27 :

1. L'association se fait depuis l'agent vers la connexion, après échange du jeton d'enrôlement —
   jamais en acceptant un identifiant d'agent dans un corps de requête.
2. Le dispatcher vérifie en plus, à l'exécution, que `agent.TenantId == connection.TenantId`.
   Une défense en profondeur et non une redondance : la première garantie vit dans un flux, la
   seconde dans le chemin qui envoie réellement les données.

## 6. Lots, dépendances, état

| US | Titre | Lot | État |
|----|-------|-----|------|
| INT-01 | Schéma et entités EF Core | L1 | **livré**, migration appliquée et interrogée sur PostgreSQL 18 |
| INT-02 | Contrat public et ports | L1 | **livré**, test d'architecture sur les `ProjectReference` |
| INT-03 | Connexions par tenant | L1 | **livré** |
| INT-04 | Tables de correspondance | L1 | **livré**, réserve sur `unmapped` (voir §8) |
| INT-05 | Commandes idempotentes et cycle de vie | L1 | **livré** |
| INT-06 | Dispatcher Hangfire multi-tenant | L1 | **livré**, + reprise des revendications abandonnées (hors CA, voir §8) |
| INT-07 | Correspondance des identifiants | L1 | **livré** |
| INT-08 | Journal d'appels et audit | L1 | **livré** |
| INT-09 | Résilience | L1 | **livré**, réserve sur le health-check (voir §8) |
| INT-10 | Adaptateur factice et tests de contrat | L1 | **livré**, 7 suites abstraites héritées par FakeAdapter |
| INT-11 | Permissions RBAC | L1 | **livré**, réserve sur le test par endpoint (voir §8) |
| INT-12 | Adaptateur Temenos : clients et KYC | L2 | **livré** — dernier critère invérifiable, voir §9 |
| INT-13 | Adaptateur Temenos : comptes et soldes | L2 | **livré** — idem |
| INT-14 | Onboarding de bout en bout | L2 | **livré** — critère 2 à moitié, voir §10 |
| INT-15 | Solde en temps réel | L2 | **livré** |
| INT-20 | Synchronisation incrémentale | L3 | **livré** — réserve sur le test HTTP, voir §10 |
| INT-21 | Snapshot client | L3 | **livré** — port de lecture ajouté, voir §10 |
| INT-22 | Surveillance des plafonds | L3 | **livré** — critère 4 hors module, voir §10 |
| INT-24/25 | Socle batch | L4 | **livré** — un défaut de perte d'écritures corrigé, voir §11 |
| INT-26/27 | Agent relais | L4 | **livré** — une obligation reste hors module, voir §11 |
| INT-28 | Perfect Vision | L4 | **bloquée** — spécification d'interface absente ; le refus est livré et vérifié, voir §11 |
| INT-31 | Amplitude | L5 | **livré dans la limite de SBS** — CA 2 et 3 tenues, CA 1 et 4 bloquées, voir §12 |
| INT-32 | SAB AT | L5 | **livré dans la limite de SBS** — CA 2 tenue, CA 1 partielle, CA 3 bloquée, voir §12 |
| INT-34 | Réconciliation quotidienne | L7 | — |
| ASS-01…12 | Famille assurance | L8 | ASS-06 **bloquée** — spécification ORASS absente |

## 9. L2 — ce que l'absence de sandbox Temenos empêche

L'API Party/Holdings de Transact est documentée publiquement, donc l'adaptateur **s'écrit**. Ce qui
manque est l'environnement : pas d'URL, pas d'identifiants, et le document OpenAPI de Transact
n'est pas redistribuable — d'où l'absence d'`OpenApiReference` dans le csproj, contrairement au
client biométrique de M02 qui est généré.

Conséquences assumées, et elles portent sur deux critères seulement :

- **INT-12.5 et INT-13.4** (« la suite de tests de contrat passe contre le sandbox ») sont
  **préparés, non vérifiés**. L'adaptateur hérite des mêmes suites de contrat abstraites que
  `FakeAdapter`, exécutées sur un transport HTTP bouchonné avec des corps JSON enregistrés ; et un
  test d'intégration dormant, piloté par `SANKORE_TEMENOS_*`, s'active le jour où l'environnement
  existe. Le patron est celui de `R2ObjectBackendIntegrationTests`, déjà dans ce dépôt.
- **INT-14.5**, moitié Temenos : même réserve. La moitié `FakeAdapter` est, elle, un vrai test de
  bout en bout.

Le risque résiduel est nommé plutôt que masqué : les enregistrements de la couche fil sont écrits à
la main depuis la documentation publique, et M02 a appris ce que cela coûte quand les noms ne
correspondent pas — chaque champ mappé à null, une erreur générique, un service qui répondait
parfaitement. C'est la première chose à confronter à une installation réelle, et le commentaire en
tête de `TemenosWire.cs` le dit.

## 10. L2 et L3 — décisions prises à la réconciliation

### Un port ajouté à la liste d'INT-02 : `ICbsKycLevelPort`

INT-21 a buté sur un fait que la liste de ports de la spécification ne couvre pas :
`ICbsCustomerPort` sait **écrire** le niveau KYC et n'offre rien pour le **relire**. Sans lecture,
le critère 4 (« un écart entre le niveau KYC du CRM et celui du CBS ») ne peut détecter qu'un cas :
« nous avons poussé et ça a échoué » — déjà visible dans la file de commandes. L'écart qui intéresse
la conformité, un niveau changé **dans** le CBS par un agent de l'IMF, restait invisible.

Retenu : une **interface optionnelle** `ICbsKycLevelPort` plus une capacité `ReadKycLevel`, et non
un membre ajouté à `ICbsCustomerPort`. Les adaptateurs diffèrent là-dessus en nature, pas en
qualité — Transact expose le statut KYC d'un tiers, un CBS par fichiers accepte l'écriture et
n'offre aucune requête. Mettre le getter sur le port client aurait forcé la moitié des adaptateurs
à répondre « non supporté », et aurait cassé tous les adaptateurs existants le jour de l'ajout.

Le projecteur préfère le port quand l'adaptateur le déclare, et retombe sinon sur la déduction. Un
port **en échec** n'entraîne pas de repli : les deux répondent à des questions différentes, et
substituer l'une à l'autre rapporterait un écart comme résolu alors que la lecture a seulement
échoué. Trois tests gardent ces propriétés.

### Deux inquiétudes inter-chantiers qui n'en étaient pas

Le cloisonnement a un coût : chaque agent ne voit que sa tranche. Deux alertes remontées se sont
révélées déjà couvertes par un chantier voisin, et il valait mieux vérifier que corriger.

- « Aucun moyen d'écrire le secret du webhook, donc INT-20 échoue fermé mais inutilisable » —
  faux : `ConnectionSecretNames` d'INT-03 inclut déjà `webhook-secret`.
- « La fenêtre de flux d'INT-22 et celle du snapshot peuvent mesurer des spans différents » —
  faux : `SnapshotFlowWindow` lit `KycLimits.WindowDays` de M02 et documente exactement ce risque.

### Une décision d'hôte : la limitation de débit du webhook

Le chantier sync avait réutilisé la politique `"ingest-key"` faute de mieux. Elle partitionne sur la
valeur de route `publicKey`, que cette route n'a pas — donc **tous** les webhooks d'intégration
arrivant sur une instance partageaient une seule fenêtre de 10/min, et l'étranglement se lirait
comme un CBS silencieux. Politique dédiée `"integration-webhook"`, partitionnée IP + `connectionId`,
60/min : le travail déclenché est une re-projection idempotente d'un client déjà connu, et la
signature HMAC est ce qui empêche un appelant non authentifié de la consommer.

### INT-09 : le disjoncteur n'existait que sur le papier

Le chantier Temenos a signalé, à juste titre, qu'il n'avait pas branché
`IntegrationResiliencePipelineProvider` sur son transport — les politiques Polly d'INT-09 étant ses
critères et non les siens. Conséquence réelle : **aucun adaptateur vivant ne passait par le
pipeline**, donc aucun circuit ne s'ouvrait jamais, le health-check du module rapportait un état
inconnu pour toutes les connexions, et une installation Transact qui avait cessé de répondre était
rappelée à chaque commande. Les critères 1 et 4 d'INT-09 étaient déclarés, pas en vigueur.

Branché : pipeline de **lecture pour un GET**, d'**écriture sinon** — INT-09 ne veut le retry court
que sur les lectures directes, et rejouer une écriture ici la doublerait, le dispatcher possédant
déjà ce budget.

Deux défauts que ce branchement a révélés, tous deux introduits par lui :

1. **Un `HttpRequestMessage` ne peut pas être envoyé deux fois.** La requête était construite hors
   du pipeline, donc le premier retry levait `InvalidOperationException` au lieu de retenter : le
   retry était configuré et mort. Elle est désormais reconstruite à chaque tentative.
2. **Polly signale un circuit ouvert en levant.** Sans interception, l'exception s'échappait de la
   méthode de port, et ouvrir le disjoncteur — une protection — convertissait une panne gérée en
   exception non gérée dans un job Hangfire. Un circuit ouvert est maintenant un résultat
   `Transient` portant `INTEGRATION_CIRCUIT_OPEN`, attrapé **avant** le gestionnaire
   d'annulation : un circuit ouvert n'est pas un timeout, et le rapporter comme tel cacherait à
   l'opérateur la raison pour laquelle l'appel n'a pas été tenté.

Deux tests d'intégration épinglent le tout : un CBS qui échoue en boucle ouvre le circuit et son
état est lisible ; l'appel suivant est un échec `Transient` et non une exception.

### Réserves restantes, par US

- **INT-14, critère 2 (moitié)** : aucun contrat n'expose le produit choisi. Vérifié sur
  `ICustomersModule`, `IKycModule`, les deux événements et `IAdministrationModule`. La chaîne
  s'arrête après `SetKycLevel` ; aucun code produit n'est inventé. La branche `OpenAccount` existe
  et est testée derrière une couture à une méthode (`IOnboardingProductSelector`), pour qu'elle ne
  soit pas du code mort inatteignable.
- **INT-20, critère non testable** : le plomberie HTTP du webhook (template de route, anonymat,
  attachement de la politique de débit, lecture du corps) n'est pas couverte — même réserve
  qu'INT-11, aucun précédent `Mvc.Testing` dans ce dépôt. Tout ce qui est sous HTTP est testé.
- **INT-22, critère 4 : hors de ce module, et non construit.** Les consommateurs côté M02 (tâche de
  mise à niveau) et M08 (notification) **n'existent pas**. Les deux événements portent tout ce
  qu'il leur faut sauf une **devise** : M02 la détient dans `caps-currency` et
  `IKycModule.GetLimitsAsync` ne la renvoie pas. À trancher avant d'écrire le consommateur M08 :
  soit M08 la lit de M02, soit le contrat gagne un champ.
- **INT-13, le flux multi-devises** : `GetMonthlyFlowAsync` exclut les comptes dont la devise
  diffère de celle du premier compte du client, plutôt que de convertir. Sommer des XOF et des EUR
  produirait un nombre qui n'est pas un montant, et ce chiffre alimente un plafond réglementaire.
  Un flux multi-devises demande une source de taux et une décision produit.
- **INT-12, le domaine de mapping KYC manquant** : `MappingDomain` n'a pas de domaine pour les
  grades KYC, donc le vocabulaire Transact (`NOT_STARTED`/`SIMPLIFIED`/`FULL`) est codé dans
  `TemenosWire.cs`. Ajouter un domaine est une décision de schéma, hors INT-12.
- **INT-12/13, le risque résiduel nommé** : les enregistrements de la couche fil et le transport
  bouchonné des tests ont été écrits ensemble depuis la même documentation publique. Ils
  s'accordent parce qu'ils ont la même source, pas parce que l'un a vérifié l'autre — donc aucun
  nombre de tests verts ne peut contredire une erreur qu'ils partagent. Trois points à confronter
  en premier à une installation réelle, listés dans la bannière de `TemenosWire.cs` : le paramètre
  de recherche `mnemonic`, l'existence de la sous-ressource `kycStatus`, et le respect de
  `Idempotency-Key`.
- **INT-22, une lecture assumée** : dédupliquer `KycLimitExceeded` par mois n'est pas dans le
  critère, qui ne l'énonce que pour `Approaching`. Sans cela un client durablement au-dessus du
  plafond produirait un événement — et une tâche de mise à niveau — chaque nuit.

### Ce que « bloqué » signifie exactement

Quatre adaptateurs (INT-28, INT-31, INT-32, ASS-06) décrivent un format de fichier ou une API
qu'aucun document ne définit ; leurs propres CA le disent (« Prérequis : spécification obtenue —
question ouverte »). Pour chacun, le livrable possible sans le tiers est : le projet d'adaptateur,
sa matrice de capacités, son enregistrement keyed, et un échec `Technical` explicite nommant la
pièce manquante — pas un mapping inventé qui compilerait et mentirait.

INT-12/13 sont différents : l'API Party/Holdings de Temenos Transact est documentée publiquement,
donc l'adaptateur s'écrit ; seule la dernière CA (« tests de contrat verts contre le sandbox »)
demande des identifiants. La suite de contrat tournera contre un double, et un test
d'intégration dormant, piloté par variables d'environnement, s'activera le jour où le sandbox
existe — le patron `R2ObjectBackendIntegrationTests` du dépôt.

Hors périmètre, confirmé par la spécification : l'import initial d'un portefeuille existant.

---

## 7. Vérifications effectuées sur le socle

| Quoi | Comment | Résultat |
|------|---------|----------|
| Les 10 tables et leurs index | `dotnet ef database update` sur une base jetable PostgreSQL 18 | 10 tables + outbox + inbox créées ; les 9 index uniques de la spécification présents avec les bonnes formes |
| `integration_call_log` partitionné | `\dt integration.*` | **table partitionnée** + 4 partitions mensuelles (mois courant + 3) |
| Le routage de partition | `INSERT` puis `tableoid::regclass` | la ligne atterrit dans `integration_call_log_y2026m10` |
| La partition manquante **échoue** | `INSERT` à +10 mois | `ERROR: no partition of relation "integration_call_log" found for row` — ce qui établit que `EnsureCallLogPartitionsJob` fait partie de la fonctionnalité, pas de l'entretien |
| L'asymétrie ASS-01 | deux connexions CoreBanking actives, puis deux Insurance actives | la 2ᵉ CoreBanking est refusée par `ux_integration_connection_active_core_banking` ; les deux Insurance passent |
| La suite du module | `dotnet test` | **1 156 tests, 0 échec, 0 avertissement** (414 au L1, 686 au L3, 975 au L4) ; + 48 pour `Sankore.Integration.RelayAgent` |
| La solution entière | `dotnet build SankoreCRM.sln` | 0 erreur |
| Les autres modules | les 9 autres suites | tout passe, sauf 2 échecs **préexistants** confirmés dans un worktree sur HEAD (`ActivateTemplateHandlerTests` — EF InMemory ne gère pas `ExecuteDelete` ; `CustomersModuleCompositionTests` — `ICustomerModule` non résolu) |
| Le câblage de l'hôte | `--emit-openapi` (démarre réellement l'hôte) | 323 routes dont **26 pour Integration** (23 sous `api/v1`, 3 publiques : enrôlement, heartbeat, webhook) ; aucune erreur de DI au démarrage |
| Les adaptateurs embarqués sont joignables | `IntegrationAdapterCompositionTests` (`Sankore.Api.Tests`) | les 5 enregistrements attendus présents, une seule fois chacun, en `Scoped` ; le double en Development seulement, dans les deux sens. Vérifié en retirant un appel : le test tombe |
| Les trois commutateurs `$kind` couvrent l'enum | `ConnectionSettingsJsonConverterTests` + `ConnectionSettingsConverterTests` | les 6 `IntegrationKind` ont un enregistrement de réglages, font l'aller-retour HTTP **et** jsonb, et portent le bon discriminant |
| Aucun endpoint orphelin | chaque `*Endpoint.cs` du module confronté aux agrégateurs appelés par `IntegrationModule` | les 38 fichiers appartiennent tous à un agrégateur mappé ; `Batch`, `KycLimits`, `Onboarding`, `Snapshot`, `Dispatch` n'exposent volontairement aucune route (jobs et consommateurs) |
| Aucun secret exposé | inspection des schémas de réponse du document OpenAPI | aucune propriété évoquant un secret, hors `maskedValue` / `isConfigured` / `vaultRef` |

---

## 8. Réserves à lever, par US

Rien de ce qui suit n'est un critère d'acceptation non tenu par négligence ; chacun est une limite
de l'environnement ou une dépendance manquante, et chacun est visible dans le code.

**INT-04, dernier critère partiel.** « Un endpoint liste les codes CRM sans correspondance, par
domaine » suppose une énumération des codes du CRM. `IAdministrationModule` n'expose pas de
catalogue : il donne les agents disponibles, une agence par id, une catégorie de produit par code.
Le résultat porte donc un indicateur `Partial` / `Complete` : un domaine non énumérable renvoie
explicitement une liste incomplète plutôt qu'une liste vide qui se lirait « tout est mappé ».
Lever la réserve demande une méthode d'énumération sur le contrat M12 — une US à part.

**INT-09, dernier critère partiel.** L'état du disjoncteur est exposé par un `IHealthCheck` nommé
`integration`, le premier qu'un module de ce dépôt contribue. Mais `/health` détaillé n'est mappé
qu'en Development (`Sankore.Api/Infrastructure/ServiceDefaults.cs`) : en production le `/health`
simple ne rapporte rien. Le check existe et fonctionne ; le rendre visible hors Development est un
changement d'hôte, délibérément laissé hors de ce lot.

**INT-11, dernier critère partiel.** « Test d'autorisation par endpoint » : ce dépôt n'a aucun
précédent de lecture des métadonnées d'endpoint, et aucun projet ne référence `Mvc.Testing`. Le
livrable est le test de catalogue (les 15 permissions déclarées, aucune de plus, aucune en double)
plus l'attribut `RequireAuthorization` sur chaque endpoint, vérifiable à la lecture. Un vrai test
par endpoint demande d'introduire `Mvc.Testing` et un `WebApplicationFactory`, ce qui bénéficierait
à tout le dépôt — US à part.

**INT-14, une perte possible, assumée et à refermer en L7.** Le garde d'inbox est réclamé *avant*
la tentative, et les échecs de la chaîne sont journalisés puis avalés. Conséquence : un KYC validé
pour un tenant dont aucune connexion CBS n'est active **perd cet onboarding** — l'événement est
acquitté et jamais redélivré. L'alternative (relancer l'exception) ferait redélivrer indéfiniment
contre une condition que seul un administrateur peut lever. Le remède est la réconciliation
d'INT-34 : un client actif avec un `integration_reference` manquant est exactement un écart
`MissingInExternal`, qui est déjà un type de la spécification. À épingler par un test quand L7
sera écrit.

**INT-14, le défaut du garde d'inbox, corrigé.** `IntegrationInboxGuard` s'appuyait uniquement sur
`catch (DbUpdateException)`. Le provider EF InMemory lève une `ArgumentException` **nue** depuis
`SaveChangesAsync`, donc le filtre ne s'armait jamais et un événement rejoué faisait planter le
consommateur dans tout test l'utilisant — la production sur PostgreSQL n'était pas touchée, ce qui
est précisément ce qui rend ce genre de défaut durable. Aligné sur `InboxGuard` de M01 : lecture
d'abord, `catch` réservé à la vraie course concurrente, et le filtre resserré à PostgreSQL seul
(l'ancien avalait aussi n'importe quelle `ArgumentException` légitime).

**INT-10, câblage de l'hôte.** `AddFakeAdapter` échoue *fermée* : hors Development elle lève, au
lieu de sauter l'enregistrement en silence. Un saut laisserait la connexion répondre
`ADAPTER_NOT_REGISTERED` à la première commande — des heures plus tard, dans un log de job, loin du
déploiement fautif. L'hôte garde l'appel en plus, donc la décision d'offrir le double et le refus
de l'adaptateur sont deux verrous indépendants.

**INT-06, au-delà des critères.** Les CA ne parlent que des commandes `Pending` et
`RetryScheduled`. Un worker tué — OOM, éviction, déploiement — laisse la sienne en `Sending`, et
rien dans le balayage spécifié ne regarde plus jamais ce statut : l'écriture est due pour toujours,
sans erreur nulle part. Ajouté : `next_attempt_at` porte, pour une commande en vol, l'expiration de
la revendication, et une revendication expirée redevient éligible. Aucune colonne nouvelle, et un
worker vivant n'est jamais dépossédé. Le `catch` du balayage couvre le cas où l'appel lève ; il ne
peut rien pour un processus mort, et la documentation qui l'affirmait a été corrigée.

---

## 11. L4 — décisions prises à la réconciliation

### Un défaut de **perte d'écritures** dans le mode batch, corrigé

Le défaut est né de la rencontre de deux pièces correctes prises séparément. Avec une coupure à
18 h 00 UTC et une commande créée à 09 h 00 :

1. `OutboundBatchCycle.CurrentCutOff(09:00, 18:00)` renvoie **18 h 00 la veille** — le cycle
   courant est bien celui-là, et le générateur n'y est pour rien ;
2. le crible d'éligibilité est `CreatedAt <= cutOff`, donc la commande du matin **n'en fait pas
   partie** ;
3. l'enrôleur renvoyait un échec `Transient`, le dispatcher appelait `ScheduleRetry`, et les 8
   tentatives plafonnées à une heure étaient **consommées avant midi** ;
4. le statut `Rejected` n'est **pas** dans l'ensemble éligible du générateur — donc le fichier de
   18 h 00 partait sans elle, et l'écriture ne quittait jamais la plateforme.

Sur un cycle quotidien, c'est la majorité des commandes : le mode batch était inutilisable, et le
symptôme — une file de rejets avec un message parlant de coupure — se lisait comme une
configuration à revoir plutôt que comme une perte.

Le remède tient en trois pièces, et sa forme est dictée par le fait que l'attente **n'est pas une
tentative** :

- `IntegrationErrors.BatchCycleNotDue`, distinct de `Unavailable`. La distinction est portante, pas
  cosmétique : c'est le seul signal qui permet au dispatcher de faire la différence entre « le
  système d'en face n'a pas répondu » et « l'heure n'est pas venue ».
- `IntegrationCommand.DeferUntil(dueAt, …)` — `Sending → RetryScheduled`, `NextAttemptAt` posé
  **sur la coupure** et non sur la courbe de retry, et le compteur de tentatives **décrémenté**.
  Décrémenté et non « sauté », parce que `BeginSending` a déjà pris la tentative avant que
  quiconque puisse connaître le mode de la connexion : le mode n'est lisible qu'après résolution.
  Plancher à zéro, pour qu'un report ne prête jamais une tentative.
- l'instant voyage dans le `Detail`, préfixé en forme aller-retour (`{instant:O}|{message}`). Le
  dispatcher le **relit** au lieu de recalculer une coupure dont il n'a pas les réglages — ce
  serait un second endroit où se tromper. Ce qui est *stocké* dans `LastErrorMessage` est la moitié
  lisible, l'heure nommée en clair.

Un préfixe malformé, ou un instant déjà passé, **retombe sur la courbe de retry ordinaire**. C'est
voulu : un report vers un instant révolu ferait tourner le dispatcher en boucle sans budget pour
l'arrêter, alors que la courbe dépense ses tentatives et finit par un rejet visible — le bon mode
d'échec pour un défaut dans le contrat du générateur.

Épinglé par `BatchCutOffDeferralTests` (5 cas). Deux précautions dans l'écriture de ces tests :
l'assertion porte sur le **compteur de tentatives**, pas sur le statut — `RetryScheduled` est aussi
ce que produisait le code fautif, sur le chemin du rejet ; et le test de bout en bout rejoue **douze
passes** du dispatcher avant la coupure, parce que le dispatcher tourne chaque minute et qu'une
seule passe passe aussi contre le code fautif. Vérifié en neutralisant la branche : 3 des 5 cas
tombent.

### INT-27, critère 5 : une latence par cible, pas une par agent

« La latence vers **chaque** système relié » ne tient pas dans un `int? ReportedLatencyMs`. Ajouté
`reported_targets` (jsonb) avec sa migration ; le scalaire garde la **pire** latence et non une
moyenne — un agent à 40 ms du CBS et 9 s du serveur SFTP n'est pas en bonne santé, et c'est la
colonne sur laquelle une console trie.

Conséquence sur le test de liste blanche des champs d'INT-27 : il a échoué quand `Targets` est
entré dans le contrat de heartbeat, **par construction**. Vérifié que `Targets` ne porte que les
noms, types et latences des cibles déclarées par l'agent lui-même — aucune donnée d'identité — puis
la liste blanche a été élargie **avec sa justification écrite**, et `RelayTargetHealthReport` ajouté
à `ClientBoundTypes` pour que ses champs affrontent eux aussi les assertions sur les fragments
d'identité.

### Une obligation d'INT-26 qui ne peut pas être tenue dans ce module

`IRelayAgentAdmission.AdmitAsync` doit être appelée **à chaque message**, pas une fois à la
connexion : c'est ce qui rend vraie la « révocation coupe la session immédiatement » du critère 2.
La révocation efface l'empreinte ici, mais rien dans ce module ne peut fermer une socket qu'il ne
possède pas — d'où l'interface publique et l'obligation écrite dans
`RelayAgentsServiceRegistration`.

Vérifié plutôt que supposé : `AdmitAsync` lit la table à chaque appel, sans cache ni mémoïsation
d'aucune sorte, et son seul appelant actuel (le heartbeat) l'appelle par requête. Il n'y a donc rien
à corriger — mais l'hôte du canal WebSocket, quand il sera écrit, hérite de l'obligation.

### INT-28 : pourquoi le refus suffit, et comment on le sait

L'adaptateur Perfect Vision refuse chaque opération en nommant le document manquant, sans inventer
de mapping. La question qui restait est plus intéressante que l'adaptateur : **le chemin batch ne
passe pas par un adaptateur**. Le dispatcher aiguille sur le mode *avant* de résoudre l'adaptateur,
donc une connexion Perfect Vision en mode `Batch` écrirait un fichier au format délimité
générique — un format inventé, exactement ce que le refus existe pour empêcher.

Ce chemin est **structurellement** inatteignable, et par deux verrous indépendants :

1. `CheckHealthAsync` répond toujours `Unhealthy`, et `IntegrationConnection.Activate` exige un
   health-check passé — une connexion Perfect Vision ne peut donc jamais devenir active ;
2. toute mise en file passe par `RequireCoreBankingAsync`, qui résout par famille **et `IsActive`** :
   aucune commande ne peut exister contre une connexion inactive.

Le premier verrou est déjà épinglé par
`PerfectVisionAdapterTests.A_failed_health_check_is_what_keeps_the_connection_inactive`, dont le
commentaire énonce précisément cet argument. 45 tests passent sur cet adaptateur.

### Dette enregistrée au L4, non traitée

- **La liste de blocage SSRF est dupliquée** — `SftpEgressGuard` ici, `SsrfSafeHandler` dans Leads.
  Un test comportemental épingle les adresses de bord des deux côtés, mais une **divergence** entre
  les deux ne serait signalée par rien. Sa place est `Sankore.Shared.Kernel`.
- **`TemenosTransport` en mode `Api` ne valide pas sa sortie** — le même raisonnement que pour le
  SFTP s'y applique : une adresse privée vue depuis ce processus est notre réseau, pas celui de
  l'IMF.
- **`AddHangfireServer` n'est pas découpé par file** — un transfert SFTP peut retenir un worker
  plusieurs minutes, et les files d'intégration partagent le pool par défaut.
- **`Currency` manque aux événements de plafond KYC** — M02 la détient dans `caps-currency`, mais
  `IKycModule.GetLimitsAsync` ne la renvoie pas.
- **Les consommateurs d'INT-22 critère 4** (tâche de montée en gamme M02, notification M08) et
  **l'hôte du canal WebSocket d'INT-26** restent à écrire, côté plateforme.

---

## 12. L5 — décisions prises à la réconciliation

Les deux US du lot sont bloquées sur le même tiers (SBS), et comme pour INT-28 le livrable possible
sans lui n'est pas « rien » : c'est le projet d'adaptateur, sa matrice de capacités, son
enregistrement keyed, et un refus explicite nommant la pièce manquante. Là où L5 diffère d'INT-28,
c'est que chacune des deux US a **une critère d'acceptation entier que le tiers ne conditionne pas**,
et que c'est le critère le plus intéressant des deux.

### INT-31 — ce que la version décide, et où la règle vit

Deux CA sur quatre sont tenues :

- **CA 3, la matrice selon la version.** `AmplitudeCapabilityMatrix` est une fonction pure de
  `AmplitudeSettings.AmplitudeVersion` et du mode de la connexion. Les deux versions déclarent les
  mêmes cinq écritures et ne diffèrent que par le mode — ce qui *est* l'énoncé du critère ; Up
  ajoute `ReadAccounts`, `ReadBalance`, `ReadLoans`.
- **CA 2, la bascule vers le socle batch.** `AmplitudeCarrierRouting`, fonction pure également.

La décision de fond est que **les deux champs gouvernent des chemins différents**, et ce n'est pas
une approximation : `ExecuteIntegrationCommandHandler` dévie une commande sur le *mode* avant même
de résoudre un adaptateur, donc **les écritures suivent le mode** ; le chemin de lecture
(façade → résolveur → adaptateur) ne consulte jamais le mode, donc **les lectures suivent la
version**. Perfect Vision le prouve déjà : une connexion en mode `Batch` dont la vue est déclarée
`RealTime`. Aucune lecture n'est jamais `Batch` — il n'existe pas de `CommandType` pour une lecture,
donc rien ne pourrait en différer une vers un cycle de fichiers.

`Legacy` + `Api` et `Legacy` + `Relay` sont **incohérents** (aucune API à appeler). La règle a été
placée dans le **health-check de l'adaptateur et non dans le validateur**, et c'est une décision
assumée : un 422 ne protège pas une ligne qui n'est pas passée par l'endpoint, l'idiome du module
est que la création est libre et que **l'activation est le verrou**, et surtout le contrat publié
d'INT-03 admet explicitement la combinaison — deux tests existants l'affirment
(`CreateConnectionValidatorTests`, `UpdateConnectionValidatorTests`). La chaîne de garantie est
donc la même que pour les adaptateurs bloqués : ligne incohérente → health-check `Unhealthy` →
activation refusée → aucune commande ne peut être mise en file. Ajouter le 422 changerait un
contrat publié sans ajouter de garantie.

### INT-32 — l'`Entity` est une propriété de sécurité, pas une formalité

Sur un réseau multi-IMF comme le CIF, une installation Open SAB sert plusieurs institutions et
chaque appel doit dire laquelle. Un appel sans `Entity` atterrit sur celle qu'Open SAB prend par
défaut ; un appel avec la mauvaise lit ou écrit les clients d'une autre institution — une fuite
inter-tenant **sans qu'aucune frontière HTTP de notre côté ne soit franchie**.

Le validateur rendait déjà `settings.entity` obligatoire à la configuration (L1). La moitié L5 est
**au moment de l'appel** : le contrôle d'`Entity` est la première étape du seul chemin par lequel
les dix méthodes de port produisent un résultat, avant le credential et avant le refus de
catalogue — dans l'ordre d'un appel réel (cadrer → authentifier → construire), pour que le jour où
les refus tombent un par un le contrôle de périmètre soit déjà sur le chemin. Il **refuse** en
l'absence d'`Entity`, et la matrice se réduit à `None`. Le sens de l'échec est le point : laisser
passer risque les clients d'une autre institution.

Ce que ce garde ne peut pas faire est écrit dans le code : **l'`Entity` n'est pas vérifiable** sans
le catalogue. Une présence, oui ; un code bien formé appartenant à une autre institution, non.
L'obligation pour le jour où le catalogue arrive est nommée dans `SabEntityScope` — le health-check
devra *vérifier* l'entité contre l'installation et garder l'activation fermée jusque-là.

Aucune règle de forme n'a été inventée (longueur, charset, préfixe) : chacune serait une propriété
du catalogue, et une règle inventée ici refuserait une entité légitime. Un test l'épingle, pour que
l'ajout soit délibéré.

La clé API est sondée via `GetHintAsync` et **jamais** `GetValueAsync` : rien ne peut être fait de
la valeur avant que le catalogue dise comment elle voyage, donc la déchiffrer serait une exposition
gratuite. `CREDENTIAL_MISSING` reste distinct du refus de catalogue — propriétaire différent, écran
différent.

### Trois défauts trouvés par le lot, et corrigés

**1. Le mode `Relay` n'était pas dévié au dispatch.** `ExecuteIntegrationCommandHandler` ne testait
que `Mode == Batch`, donc une connexion `Relay` tombait dans `ResolveAdapter` et le dispatcher
appelait le CBS **directement, depuis ce processus** — la seule chose que ce mode existe pour
empêcher, puisque sa prémisse est que le CBS est dans le réseau de l'institution. Sur une ligne
Temenos c'est pire qu'un appel en échec : ce transport ne valide pas sa sortie en mode `Api`, donc
un `baseUrl` sur une adresse privée faisait ouvrir au plateforme une connexion dans **son propre**
réseau, le mode faisant passer la chose pour sanctionnée. Et le chemin y menait : une connexion
Temenos vérifiée et activée en `Api`, puis basculée en `Relay` par une simple mise à jour.

Le correctif n'est pas « refuser `Relay` », parce que `Relay` a deux moitiés à des stades
différents : le porteur **fichier** est livré (`RelayFileTransport`, et
`IntegrationFileTransportRouter` envoie déjà les dépôts d'une connexion `Relay` à l'agent), le
**canal d'ordres** ne l'est pas (INT-26, côté plateforme). Donc une connexion `Relay` portant des
coordonnées batch suit exactement le chemin d'une `Batch`, et toute autre est refusée avec
`INTEGRATION_RELAY_COMMAND_CHANNEL_MISSING`, `Technical` — rien ne part d'ici en attendant. Les
deux moitiés sont épinglées : n'épingler que le refus aurait validé une version cassant le
batch-par-relais, qui est une fonctionnalité livrée.

Imprécision connue et écrite : Amplitude Up par relais (coordonnées batch, porteur API) serait
routé vers un fichier. Inatteignable aujourd'hui — cet adaptateur refuse tout — et c'est la matrice
qui en sera l'autorité le jour où le canal existera.

**2. `UpdateSettings` conservait le verdict de santé et `IsActive`.** Les colonnes de santé
décrivent la réponse du système distant **à la configuration qui a été testée**. Les traîner à
travers une modification, et elles décrivent des coordonnées qui n'existent plus : un `baseUrl`
déplacé vers le mauvais hôte, et l'écran d'exploitation continue d'afficher la connexion en bonne
santé pendant que chaque commande échoue une par une. Un vert périmé est pire qu'une colonne vide,
parce que c'est celle qu'on regarde en premier et celle qui clôt l'enquête.

Plus grave : c'est le maillon dont dépend tout l'argument des adaptateurs bloqués. INT-28/31/32 sont
sûrs parce que leur health-check ne peut pas passer, donc pas d'activation, donc aucune commande —
chaîne qui ne vaut que si « un health-check passé décrit la configuration actuelle », et rien ne le
maintenait vrai.

Un re-pointage (mode, agent relais, ou réglages différents **par valeur**) invalide désormais les
trois colonnes. `IsActive` est **délibérément laissé tel quel** : cette méthode porte aussi le NOM,
donc désactiver sur édition arrêterait l'intégration d'un tenant parce que quelqu'un a corrigé une
faute dans un libellé. Un simple renommage conserve le verdict — sinon on apprend à l'administrateur
à cliquer à travers l'avertissement, et la fois où il compte, il le fera aussi.

**3. Aucune garde ne liait les adaptateurs embarqués à l'hôte.** Perfect Vision était dans la
solution, référencé par rien, avec 45 tests verts et aucun chemin entre une connexion et son
adaptateur. Trois trous de la même famille, tous les trois désormais gardés :

| Oubli | Symptôme | Garde |
|-------|----------|-------|
| `Add…Adapter` jamais appelée | `ADAPTER_NOT_REGISTERED` — lu comme « build incomplet » | `IntegrationAdapterCompositionTests`, exhaustif sur l'enum, avec un verdict motivé par type |
| commutateur `$kind` HTTP incomplet | `settings` arrive `null` → 422 sur un champ correctement rempli | `ConnectionSettingsJsonConverterTests` |
| commutateur `$kind` EF incomplet | ligne écrite puis **relue `null`** — la connexion perd ses coordonnées en silence | `ConnectionSettingsConverterTests` |

Le bloc d'enregistrement a été extrait de `Program.cs` vers
`Sankore.Api/Infrastructure/IntegrationAdapterComposition.cs` pour que la garde ait une cible
unique. Vérifié en retirant l'appel Perfect Vision : le test tombe.

Deux faux positifs trouvés par mes propres gardes et corrigés : `TemenosSettings.TokenEndpoint`
signalé comme secret (c'est une URL — « …Endpoint » est désormais une forme sanctionnée, à côté de
« …VaultRef »), et `ServiceDescriptor.ImplementationType` qui vaut `null` sur un descripteur keyed,
donc l'orthographe évidente comparait cinq `null` et passait quoi qu'on enregistre
(`KeyedImplementationType`).

### Constats signalés, non corrigés — hors périmètre du lot

- **`AesSecretsModule.GetValueAsync` ignore `ExpiresAt`.** Un secret stocké avec une expiration est
  rendu tel quel après la date ; `GetHintAsync` rapporte la date et rien ne l'applique. Trois
  chemins posent une expiration non nulle (`SetConnectionSecret` d'Integration, `SetSecret` et
  `RotateHmacSecret` de M13), donc l'API accepte un contrôle de durée de vie qui ne fait rien.
  **Non corrigé volontairement** : c'est de l'infrastructure partagée, l'appliquer ferait cesser de
  fonctionner tout secret expiré dans quatre modules, et aucune pièce de L5 n'en dépend —
  l'adaptateur SAB s'aligne délibérément sur le comportement actuel et le dit. Le partage correct
  serait `GetValueAsync` qui refuse et `GetHintAsync` qui continue de rapporter, sans quoi le
  correctif est indiagnosticable.
- **La fenêtre de double acceptation d'`RotateHmacSecret` est morte.** L'ancien secret est archivé
  sous `hmac-signing-old` avec 7 jours d'expiration, et **rien ne le relit** : la vérification de
  webhook n'utilise que le secret courant. Donc la rotation casse immédiatement toute intégration
  partenaire, alors que le handler renvoie `oldExpiresAt` à l'appelant comme s'il existait une
  fenêtre. M13, préexistant, et corriger demande de toucher la vérification de signature.
- **`IntegrationMode` n'est pas contraint par `IntegrationKind`.** Rien n'empêche une connexion
  Temenos en mode `Batch` ni une Perfect Vision en mode `Api`. Le défaut 1 ci-dessus en fermait la
  conséquence dangereuse ; la cohérence générale mode/type reste non modélisée, et chaque adaptateur
  la traite dans son health-check.
- **Le dépôt ne compile pas sans avertissement**, contrairement à ce que j'avais affirmé : les
  projets Integration, oui (les 3 avertissements d'analyseur de son projet de tests sont corrigés —
  CA5351 supprimé localement avec sa justification, CA1825 et CA1826 réparés), mais M13 en porte
  110, `Notifications.Tests` 78 et M12 28, tous préexistants et hors périmètre.
