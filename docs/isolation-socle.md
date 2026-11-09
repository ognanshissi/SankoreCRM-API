# Isolation du socle

**Statut : audit du 2026-11-09, plan validé le 2026-11-09. Lots 1 à 5 livrés et vérifiés (non commités) : plan terminé.**

## 1. Le besoin

Un tenant « CRM seul » (aucun module) doit être un CRM générique : aucune notion de banque, de microfinance ou d'assurance, ni dans les écrans, ni dans les règles, ni dans les rôles. C'était la règle du découpage en modules du 2026-10-23 (« Banking - Microfinance » et « Assurance », le reste est le socle). Des fonctions ajoutées depuis ont échappé aux gardes de module.

## 2. Décisions du propriétaire (2026-11-09)

| Sujet | Décision |
|---|---|
| Rôles finance (analyste crédit, gestionnaire de crédit, caissier, gestionnaire de comptes) | Plus créés pour un tenant sans le module ; ajoutés à l'activation. Masqués sur les tenants socle existants, sans être supprimés. |
| Revue KYC à l'anniversaire du contrat (règle CIMA) | Réservée aux contrats d'assurance. |
| Moyens de paiement du client | Restent dans le socle : utiles à toute entreprise qui vend. Libellés neutres. |

## 3. Ce qui est déjà bien isolé

- **Intégrations :** connexions core banking et assureur, polices, sinistres, relevés, réconciliation, solde en direct.
- **Fonctions bancaires :** groupes solidaires et tontines, route et bouton « Dossier de crédit ».
- **Catalogue :** catégories de produit à la création, questionnaires de qualification.
- **Fiche client :** les onglets Contrats et Documents ne lisent l'assureur et le core banking que si le module est actif.
- **Navigation et événements :** webhooks, menu latéral, « Ma journée », création rapide, cloche.
- **Traitements du core banking :** création du client au core banking après le KYC, souscription d'assurance (les consommateurs vérifient le module).

## 4. Ce qui fuit

| Fonction | Module | Ce qui fuit |
|---|---|---|
| Gabarits de projet | Les deux | Natures de montant crédit / prime / dépôt et modèles « Crédit », « Assurance », « Épargne », « Crédit de campagne » ouverts à tous. Un champ `dureeMois` en fait un dossier de crédit (signataires séparés, envoi au core banking, panneau « Envoyer au core banking »). |
| Workflows | Les deux | L'étape « Action Integration » vers le core banking ou l'assureur s'enregistre et s'active ; le dossier ne peut alors plus être approuvé. |
| Distribution d'assurance | Assurance | Profil distributeur et cartes professionnelles CIMA ouverts côté serveur, et onglet de la fiche utilisateur. |
| Produits | Les deux | Carte « Lier au Core Banking » sur tout produit ; onglet « Politique bénéficiaires » avec rôles d'assuré et consentements crédit et assureur. |
| Fiche client | Assurance | « Termes du contrat » (capital assuré, prime, taux) et clause bénéficiaire modifiables sur tout contrat ; contrats d'assurance bloqués en synchronisation listés pour tous. |
| Leads | Les deux | Consentement aux données de santé ; consentements « Bureau d'Information sur le Crédit » et « assureur ». |
| Rôles et droits | Les deux | Rôles finance et droits finance (`loan:create`, `customers:holding_manage`, `customers:groups_manage`…) créés pour tous. |
| Tâches de fond | Assurance | 12 tâches assurance par tenant (sans effet faute de données) ; revue KYC anniversaire sur tous les contrats. |
| Paramètres | Assurance | « Conservation des réponses santé » dans les paramètres des leads. |
| Mise en route | Banking - Microfinance | La checklist exige une connexion core banking. |
| Front | — | Une liste de modules absente est lue comme « tous les modules ». |
| Démarrage et démo | Les deux | La démo ignore les modules ; le démarrage socle ne crée aucun produit Standard. |
| Vocabulaire | — | Catégories de réclamation « Crédit » et « Épargne », filtres Contrats et Documents, libellés des rapports, une vingtaine d'exemples et de textes d'aide. |

## 5. Lots

| Lot | Contenu | Résultat |
|---|---|---|
| **1. Garde-fous serveur** | Gabarits (nature de montant, catégorie), envoi d'un dossier de crédit, cible « Action Integration », distribution d'assurance et cartes, consentement santé, contrats bloqués, revue anniversaire, réglage santé | Un tenant socle ne peut plus créer ni déclencher de fonction finance, même par l'API |
| **2. Rôles et droits** | Rôles et droits finance créés à l'activation du module, masqués sans lui ; catalogue des droits filtré | L'écran des rôles d'un tenant socle ne parle ni de crédit ni d'assurance |
| **3. Front** | Blocs, onglets, modèles et étapes masqués selon les modules ; liste absente lue comme « aucun module » ; checklist adaptée | Aucun écran du socle ne montre de fonction finance |
| **4. Vocabulaire** | Libellés et exemples neutres ; catégories de réclamation finance réservées aux modules ; moyens de paiement neutres | Les textes du socle sont génériques |
| **5. Démarrage et démo** | Un produit Standard au démarrage socle ; démo qui suit les modules | Un tenant socle de démo montre un CRM générique |

Recette : le tenant « Recette Socle Seul » (https://tenant-scole.sankoreio.local:4222, profil « CRM seul », marqué test) sert de témoin à chaque lot.

## 6. Lot 1 livré (2026-11-09)

Sans le module concerné, le serveur refuse désormais :

| Fonction | Règle | Réponse |
|---|---|---|
| Gabarit de projet | Montant « crédit » ou « dépôt » : Banking - Microfinance. Montant « prime » : Assurance. Famille de produits : le module de sa catégorie. Seul le passage **vers** un module est refusé : un gabarit créé avec le module reste modifiable après sa désactivation. | 403 `TENANT_MODULE_DISABLED` ou `PRODUCT_CATEGORY_MODULE_DISABLED` |
| Envoi d'un dossier de crédit | L'envoi exige Banking - Microfinance. La lecture répond « rien à montrer » (204), car la page projet la demande pour chaque projet. | 403 / 204 |
| Workflow | Un circuit avec une étape « Action Integration » vers le core banking ou l'assureur ne s'active pas sans le module. | Message en français à l'activation |
| Distribution d'assurance | Profil distributeur, pièces, cartes professionnelles et cartes qui expirent : Assurance. | 403 |
| Consentement santé d'un lead | Assurance. | 403 |
| Contrats bloqués en synchronisation | Assurance. | 403 |
| Réglage « conservation des réponses santé » | Masqué et non modifiable sans Assurance. | Absent de la liste, 404, 403 |
| Revue KYC à l'anniversaire du contrat | Contrats d'assurance seulement, quels que soient les modules. | — |

**Laissé tel quel, et pourquoi :**
- **Tâches de fond assurance :** elles restent planifiées pour tous les tenants. Sans connexion ni contrat d'assurance, elles ne font rien. Après une désactivation, elles continuent sur les contrats en cours, comme décidé le 2026-10-23.
- **Moyens de paiement :** ils restent dans le socle (décision du 2026-11-09) ; leurs libellés passent au lot 4.
- **Termes du contrat et politique bénéficiaires :** les masquer selon la catégorie relève du front (lot 3).

**Recette du 2026-11-09 :**
- **« Recette Socle Seul » (aucun module) :** les refus sont ceux du tableau, et un gabarit « montant générique » reste accepté.
- **« Démo Complète » (deux modules), témoin :** tout reste ouvert. Distribution, cartes et contrats bloqués répondent 200, et un gabarit « crédit » est accepté.
- **Données :** les deux gabarits de test ont été supprimés.
- **Tests :** Leads 1594, Workflow 201, Customers 1156, Administration 715, KYC 810, API 92.

## 7. Lot 2 livré (2026-11-09)

**Constat de départ :** les rôles intégrés (Analyste crédit, Caissier…) et le catalogue des droits sont des lignes de la plateforme, communes à tous les tenants ; seuls les rôles créés par une institution lui appartiennent. « Ne plus les créer pour un tenant socle » se fait donc en les retirant de ce que voit et fait un tenant sans le module.

**Rattachement aux modules** (`ModuleScopes`, dans le Kernel) :
| Élément | Module |
|---|---|
| Rôles Analyste crédit, Gestionnaire de crédit, Caissier, Gestionnaire de comptes | Banking - Microfinance |
| Droits `loan:create`, groupes solidaires, solde en direct | Banking - Microfinance |
| Tous les droits `Ins.*`, cartes professionnelles, lecture des réponses santé, désignation de bénéficiaires | Assurance |
| Correspondances, commandes et réconciliation d'intégration | L'un ou l'autre |
| Connexions, webhooks, contrats (`customers:holding_manage`) | Socle : la téléphonie, la messagerie et les produits Standard s'en servent |

**Sans le module** (un module désactivé garde ses rôles et droits visibles, pour ses écrans en consultation) :
- **Écran des rôles :** ses rôles intégrés n'y figurent pas, et le nombre de droits de chaque rôle ne compte pas ceux du module.
- **Catalogue et détail d'un rôle :** ses droits n'apparaissent plus.
- **Profil de l'utilisateur connecté :** il ne porte plus ces droits, donc le front ne montre plus les boutons correspondants.
- **Attribution :** la création d'un utilisateur, l'attribution d'un rôle (une à une ou en masse), l'import d'utilisateurs et les comptes de démo refusent un rôle du module (`ROLE_MODULE_DISABLED`).
- **Rôles personnalisés :** on ne peut pas leur donner un droit du module (`PERMISSION_MODULE_DISABLED`).
- **Utilisateurs existants :** celui qui a déjà un tel rôle le garde.

**Démo :** sur un tenant socle, la démo ne crée plus que 11 comptes (pas d'analyste crédit, de gestionnaire de crédit, de caissier ni de gestionnaire de comptes) et aucun dossier de crédit. Le reste de la démo suit les modules au lot 5.

**Recette du 2026-11-09 :**
- **« Recette Socle Seul » :** 6 rôles, aucun droit finance dans le profil de l'administrateur, 170 droits au catalogue au lieu de 192.
- **« Démo Complète » (témoin) :** les 10 rôles et tous les droits restent.
- **Tests :** Administration 719, Shared 507, Leads 1594, API 92.

## 8. Lot 3 livré (2026-11-09)

Le front suit les modules (`TenantModulesService`) :

| Écran | Sans le module |
|---|---|
| Liste des modules absente (profil en cours de chargement) | Lue comme « aucun module », et non plus « tous » : un tenant du socle ne voit plus passer d'écran finance au chargement. Les gardes de route attendent le profil. |
| Fiche produit | Carte « Lier au Core Banking » masquée sans Banking - Microfinance ; onglet « Politique bénéficiaires et foyer » masqué pour un produit Standard (sa politique est « aucune »). |
| Gabarit de projet | Natures de montant crédit et dépôt (Banking - Microfinance) et prime (Assurance) retirées, sauf celle déjà enregistrée ; modèles « Crédit », « Assurance », « Épargne ou compte », « Crédit de campagne » retirés. |
| Constructeur de workflows | Étape « Action Integration » absente de la palette sans aucun des deux modules ; seuls les systèmes cibles des modules actifs sont proposés (plus celui déjà choisi), et la cible par défaut suit le module. |
| Fiche utilisateur | Onglet et route « Carte professionnelle » réservés à l'Assurance. |
| Contrat d'un client | Termes selon la catégorie : capital et prime pour l'assurance, taux et dépôt pour la banque, durée et prise d'effet seulement pour un produit Standard. Clause bénéficiaire masquée quand la politique du produit ne prévoit aucun rôle et qu'aucune désignation n'existe. |
| Onglet Contrats | Filtre « Nature » réduit aux natures des modules (absent pour le socle) ; texte d'introduction neutre. |
| Page d'un contrat | Bloc « à la banque » seulement avec Banking - Microfinance ; « Actualiser le solde » seulement s'il est actif. |
| Onglet Documents | Source « Assurance » retirée du filtre ; texte d'introduction neutre. |
| Consentements du lead | « Bureau d'Information sur le Crédit » (Banking - Microfinance) et « Partage avec l'assureur partenaire » (Assurance) proposés seulement avec leur module, ou s'ils sont exigés ou déjà recueillis. |
| Questionnaire de qualification | Case « Questionnaire santé » masquée sans l'Assurance (sauf si le questionnaire l'est déjà). |
| Segmentation des clients | Règle « Encours et produits détenus » retirée sans Banking - Microfinance. |
| Vue d'ensemble du paramétrage | L'étape « Core banking » de la checklist n'existe qu'avec Banking - Microfinance. |

**Déjà réglé par le serveur (lots 1 et 2) :**
- le panneau « Envoyer au core banking » (la lecture répond « rien à montrer ») ;
- la liste des rôles et le catalogue des droits ;
- les catégories de produit proposées à la création.

**Vérification :**
- contrôle de types des bibliothèques `common`, `settings`, `customers` et `lead-management` ;
- tests de `common` : 63 ;
- compilation complète de l'application en mode développement.

## 9. Lot 4 livré (2026-11-09)

**Catégories de réclamation « Crédit » et « Épargne »** : réservées à Banking - Microfinance, partout où une réclamation entre.
- **Dépôt :** `ClientComplaintFiling`, commun à l'écran et au bot, refuse ces deux catégories sans le module (403 `TENANT_MODULE_DISABLED`).
- **Bot WhatsApp :** son menu ne les propose plus, et les numéros suivent le menu affiché (1 = « Frais et tarifs » pour un tenant du socle).
- **Front :** le tiroir de saisie ne les propose plus. Le filtre de l'onglet Réclamations les garde tant que le module est consultable.
- **Démo :** sur un tenant du socle, la réclamation « Crédit » est sautée en attendant le lot 5.

**Textes neutres dans les écrans du socle :**

| Écran | Avant | Après |
|---|---|---|
| Nouveau projet (deux tiroirs) | « Crédit pour un chantier à Daloa » | « Équipement du nouveau magasin à Daloa » |
| Import de leads, produit souhaité | « Crédit conso » | « Abonnement Premium » |
| Questionnaires de qualification | « Qualification Prêt v2 » | « Qualification nouveaux clients v2 » |
| Nom d'un produit | « Assurance vie Sérénité » | « Formule Sérénité » |
| Gabarit de projet (nom, aides) | « Parcours crédit », « crédit, assurance, compte », « crédit de campagne » | « Parcours vente accompagnée », « plusieurs rendez-vous », « une offre saisonnière, une date butoir » |
| Nouveau workflow | « Comité crédit PME » | « Validation des grands comptes » |
| Séquence de nurturing | « Relance crédit après 3 mois » | « Relance des prospects après 3 mois » |
| Fiche institution | « Institution de microfinance agréée » | « Société de distribution agréée » |
| Rapports, prévision par nature de montant | « un décaissement, une prime et une collecte » | « des montants de nature différente » |
| Moyens de paiement | « encaisser une prime » | « encaisser un paiement du client » |
| Plafonds KYC | « ouvre l'accès au crédit » | « ouvre l'accès à tous les produits » |
| KYC à distance d'un produit | « Un crédit exige… », « Exigé par CIMA… » | « Un produit à fort engagement exige… », « Exigé notamment pour une vente à distance en assurance (CIMA…) » |
| Paramétrage | « Comptes, crédits et épargne », « Relier SANKORE au core banking… », « Core banking, assurance et téléphonie » | Descriptions génériques |
| Mode test | « Le core banking et l'assureur suivent… » | « Les systèmes externes reliés suivent… » |

**Laissé tel quel :**
- **Budget du foyer :** « crédit logement », « autres crédits en cours ». Le foyer est du socle, et un emprunt personnel n'est pas une notion propre aux institutions financières.
- **Mot « souscription » :** il reste générique.
- **Libellés de natures de montant dans les rapports :** ils n'apparaissent que pour des gabarits qui en portent, et un tenant du socle n'en a plus.

**Vérification :**
- **Tests :** Customers 1158, Leads 1595 et API 92, avec de nouveaux tests sur le dépôt refusé et sur le menu du bot.
- **Contrôle de types :** customers, settings, lead-management et kyc passent.
- **En direct :** une réclamation « Crédit » sur « Recette Socle Seul » est refusée en 403.

## 10. Lot 5 livré (2026-11-09)

**Démarrage d'une institution sans module :**
- **Catalogue :** quatre produits Standard, à renommer comme les agences : « Offre Essentiel », « Offre Premium », « Installation et mise en service », « Contrat de maintenance ». Une institution avec un module garde le catalogue bancaire ou d'assurance de ses modules.
- **Questionnaire :** un questionnaire publié « Qualification commerciale » pour la famille Standard (budget, décideur, échéance, solution concurrente, besoin), à la place des questionnaires crédit, épargne et santé.

**Démo qui suit les modules :**

| Dossiers | Socle seul | Avec les modules |
|---|---|---|
| Comptes | 11 (sans analyste crédit, gestionnaire de crédit, caissier ni gestionnaire de comptes) | 15 |
| Produits demandés par les leads | Les offres Standard, avec un montant | Les produits des modules actifs (crédit, épargne, tontine ; santé, prévoyance) |
| Projets | 6, sur les offres Standard | 10, dont 4 dossiers de crédit |
| Conseiller des clients | Le premier agent de l'agence remplace les gestionnaires absents | Inchangé |
| Réclamations | « Retard de livraison de la commande » remplace la réclamation crédit | Inchangé |

Textes rendus neutres pour tous :
- **Motif de perte :** « Prix jugé trop élevé ».
- **Réclamation sur les frais :** « Frais de dossier facturés deux fois ».
- **Réponse à cette réclamation :** « trop-perçu remboursé au client ».
- **Projets :** le produit d'un lead change toutes les huit positions, si bien que les projets ne portent plus tous sur le même produit.

**Chargement interrompu, trouvé pendant la recette :** l'API a redémarré en plein chargement, qui est resté « En cours » pour toujours et bloquait tout nouveau chargement. Désormais, un chargement ou un vidage « En cours » depuis plus de 20 minutes est proposé de nouveau, puis repris (`DataLoadRules.Takeable`, `DataWipeRules.Takeable`). C'est sans risque : chaque étape est idempotente et un chargement complet dure environ trois minutes. Vérifié en direct : le chargement interrompu à 15:28 a été repris à 15:48, puis terminé.

**Recette du 2026-11-09 :**
- **« Recette Socle Démo », créée avec la démo dès la création, profil « CRM seul » :**
  - paramétrage : 4 produits Standard, le questionnaire « Qualification commerciale », 6 rôles ;
  - dossiers : 48 leads sur les 4 offres, 6 projets répartis sur les offres, 20 clients qui ont tous un conseiller, une réclamation « Retard de livraison », 12 tâches, 4 rendez-vous, KYC 3/2/1 ;
  - aucun compte analyste ;
  - « Recharger la démo » : 556 lignes effacées, même contenu.
- **« Démo Complète » (deux modules), rechargée :** contenu identique à avant (10 projets, 4 dossiers de crédit, 2 dossiers dans la file de l'analyste).
- **Tests :** Administration 720, Leads 1598, Customers 1158, registre 65.

