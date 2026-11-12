# Essai gratuit du CRM socle

**Statut : plan rédigé le 2026-11-10. Domaine choisi : `talisoftcrm.com`. Lot 0 : partie code livrée le 2026-11-10 (non commitée), réglages Cloudflare et Traefik à faire. Lots 1, 2, 3 et 4 livrés et vérifiés en local le 2026-11-11 (non commités). D3 et D4 tranchées le 2026-11-11 (D4 à préciser) ; D2, D5 à D8 en attente.**

## 1. Le besoin

Proposer le CRM socle (aucun module payant) à tout le monde, gratuitement, pendant 3 mois. Un visiteur du site Talisoft remplit un formulaire et reçoit, quelques minutes plus tard, l'adresse de son CRM et un lien pour choisir son mot de passe. Les fonctions plus poussées (Banking - Microfinance, Assurance) restent l'offre payante. Une institution passe à l'offre payante en gardant son adresse (décision du 2026-11-11).

## 2. Le principe retenu

**Une adresse générique configurée une fois, pas un sous-domaine créé à chaque inscription.**

Ouvrir une nouvelle adresse demande aujourd'hui quatre réglages :

1. un enregistrement DNS chez Cloudflare ;
2. un domaine dans Dokploy, pour que Traefik crée le routage et demande un certificat Let's Encrypt ;
3. la variable `NG_ALLOWED_HOSTS` du front : sans elle, le rendu serveur répond 400. La modifier redéploie le front ;
4. `Cors:AllowedOrigins` de l'API : en production, une origine non listée est refusée. La modifier redémarre l'API.

Automatiser ces quatre réglages à chaque inscription aurait trois défauts :

- l'inscription d'un prospect redéploierait le front et redémarrerait l'API pour toutes les institutions ;
- Let's Encrypt limite le nombre de certificats par semaine pour un même domaine ;
- le service appelé par un formulaire public détiendrait les clés Cloudflare et Dokploy, qui permettent de modifier toute l'infrastructure.

Avec `*.<suffixe>` réglé une seule fois (§ 4, lot 0), une nouvelle institution n'est plus qu'une ligne du registre. Elle est joignable dès sa création, sans rien toucher chez Cloudflare ni dans Dokploy.

La méthode « un domaine à la main » reste celle des **clients payants qui apportent leur propre domaine**.

Le domaine ne protège rien côté serveur : la connexion repose sur le jeton signé, et les fonctions sur les modules (`ModuleScopes`, gardes de module, voir `isolation-socle.md`). Le domaine sert à reconnaître l'institution avant la connexion et à séparer les sessions dans le navigateur. C'est pour cette séparation que l'on garde un sous-domaine plutôt qu'un chemin (`crm.talisoft.net/acme`). Le chemin a été écarté le 2026-11-10 : toutes les institutions partageraient le même stockage de session dans le navigateur, et il faudrait reprendre le front, le rendu serveur et le registre.

## 3. Décisions à prendre

| # | Sujet | Recommandation |
|---|---|---|
| D1 | Domaine de l'essai | **Décidé le 2026-11-10 : `talisoftcrm.com`**, adresses `https://<nom>.talisoftcrm.com`. Couvert par le certificat Cloudflare gratuit (un seul niveau). |
| D2 | Domaine de l'offre payante | **Décidé le 2026-11-11 : on garde le même nom de domaine.** L'institution reste sur `https://<nom>.talisoftcrm.com` quand elle passe à l'offre payante. `talisoftcrm.com` sert donc aux essais comme aux clients payants. Un domaine propre au client reste possible plus tard, sur demande, en adresse supplémentaire. |
| D3 | Validation des inscriptions | **Décidé le 2026-11-11 : la plateforme valide chaque demande** (« Approuver » dans l'app plateforme, le reste est automatique). |
| D4 | Durée et fin de l'essai | **Décidé le 2026-11-11.** Essai de **30 jours** depuis l'approbation (`TrialSignup:TrialDays`). Puis, comptés **depuis la fin de l'essai** (J0) : **30 jours en lecture seule** (consultation et export, aucune écriture, aucun envoi), **suspension de J+30 à J+90** (plus aucun accès, page « institution suspendue »), **effacement à J+90**, données puis institution. Des e-mails de relance annoncent l'effacement pendant la suspension (D5). |
| D5 | E-mails de relance | Proposition, à confirmer : J-7 et J-1 avant la fin de l'essai ; J0 « essai terminé, consultation seule » ; J+23 « accès suspendu dans 7 jours » ; J+30 « accès suspendu, effacement le … » ; relances d'effacement à J+60, J+83 et J+89 ; J+90 « données effacées ». Chaque e-mail propose de passer à l'offre payante. |
| D6 | Limites de l'essai | 5 utilisateurs ; 500 e-mails par mois par le relais de la plateforme (quota mensuel existant) ; pas de SMS ni de WhatsApp par les comptes de la plateforme (possible avec le compte fournisseur de l'institution, à vérifier au lot 6) ; pas de domaine personnalisé ; pas de demande de module, la page Modules affiche « Disponible avec l'offre complète — nous contacter ». |
| D7 | Informations demandées dans le formulaire | Nom de l'institution, adresse souhaitée, prénom et nom, e-mail professionnel, téléphone, pays, type d'activité (liste courte), acceptation des conditions de l'essai. |
| D8 | Qui est prévenu d'une nouvelle demande | Une liste d'adresses de l'équipe en configuration (`TrialSignup:Notifications:PlatformRecipients`), comme pour les modules. |

## 4. Les lots

### Lot 0 — Adresse générique (1 à 1,5 j)

**Code livré le 2026-11-10 (non commité)** : `Cors:AllowedOriginSuffixes` dans `CorsSetup` et `CorsSetupTests` (23 cas). Angular 22 accepte `*.talisoftcrm.com` dans `NG_ALLOWED_HOSTS` (vérifié dans `@angular/ssr`), sans changement de code. Marche à suivre côté exploitation : § 16 de `deployment-dokploy.md`. Reste à faire : les réglages Cloudflare et Traefik, puis le contrôle.

Exploitation :
- Cloudflare DNS : un enregistrement `*` (ou `*.crm`) proxifié vers l'hôte Dokploy.
- Cloudflare SSL : mode **Full (strict)** ; certificat de bord selon D1.
- Traefik : un **certificat d'origine Cloudflare** couvrant `*.<suffixe>` (gratuit, 15 ans), installé une fois, et un routeur `HostRegexp` vers le front CRM. L'onglet Domaines de Dokploy n'accepte sans doute pas `*` : label ou fichier dynamique, sur le modèle du § 14 de `deployment-dokploy.md`. La priorité du routeur doit rester sous celle des domaines explicites (`api.`, `admin.`).
- Front : `NG_ALLOWED_HOSTS=*.<suffixe>` (format accepté par Angular, à confirmer au premier déploiement).

Code :
- `CorsSetup` : nouvelle clé `Cors:AllowedOriginSuffixes` (par exemple `.essai-crm.com`). En production, une origine `https://<un seul niveau>.<suffixe>` est acceptée, et rien d'autre : pas de `http`, pas de port, pas de second niveau. Le `*` reste refusé. Tests sur les cas limites (`evil-essai-crm.com`, `a.b.essai-crm.com`, `http://`).
- `deployment-dokploy.md` : § 16 « One address per institution: `*.talisoftcrm.com` » (fait).

Contrôle : une institution créée à la main depuis l'app plateforme avec `https://recette.<suffixe>` s'ouvre, se connecte et reçoit ses e-mails avec des liens corrects (activation, mot de passe, KYC à distance), sans aucun redéploiement.

### Lot 1 — Demandes d'essai dans le registre (2 j)

**Livré le 2026-11-11 (non commité).**
- Code : `Domain/TenantSignup.cs`, `Features/TenantSignups/` (routes sous `/api/v1/tenant-signups`) et `Tenant.Offer`, exposé dans `TenantResponse.Offer`. `TrialSlug` est dans le Kernel.
- Création d'institution factorisée : `TenantCreation.AddAsync` ne sauvegarde pas, pour que l'approbation enregistre l'institution et la demande en une seule fois.
- Migration `AddTenantSignups`.
- Réglage `TrialSignup:AddressTemplate` au lieu d'un suffixe : `https://{slug}.talisoftcrm.com` en production, `http://{slug}.localhost:4222` en développement.
- Tests : 22 cas dans `TenantSignupHandlersTests` ; les 103 tests du registre passent.
- Vérifié sur le registre local avec PostgreSQL. La demande « Recette Essai » a été créée, confirmée, puis approuvée avec un jeton d'administrateur plateforme. La clé seule est refusée (403) sur les routes plateforme. L'institution a été créée en Trial, sans module, avec une fin d'essai à 30 jours. L'API a chargé les données de démarrage en 10 s. L'institution de recette est restée en place (`http://recette-essai.localhost:4222`).


Le registre (`Sankore.Admin`) garde les demandes. Il reste fermé au public : seules la clé de service (l'API) et l'administrateur plateforme y accèdent.

- Entité `TenantSignup` et migration : nom de l'institution, adresse demandée (`slug`), prénom, nom, e-mail, téléphone, pays, type d'activité, IP et date de la demande, empreinte du jeton de confirmation et son échéance, statut, motif de refus, `TenantId` une fois créée.
- Statuts : `PendingEmail` → `Confirmed` → `Approved` (institution créée) ou `Rejected`. `Expired` pour une confirmation jamais faite (48 h).
- `Tenant.Offer` : `Trial` ou `Paid`. Migration : `Paid` pour les institutions existantes.
- Routes sous clé de service :
  - créer une demande ;
  - confirmer par l'empreinte du jeton ;
  - vérifier qu'une adresse est libre (contre `tenants.fqdn`, `tenant_domains` et les demandes en cours).
- Routes sous administrateur plateforme : lister, voir, approuver, refuser.
- **Approuver** crée l'institution dans la même transaction :
  - `CreateTenantCommand` avec l'adresse `https://<slug>.<suffixe>` ;
  - le profil `CrmOnly` ;
  - `StartingData = Starter` ;
  - `TrialExpiresAt` = date d'approbation + 3 mois ;
  - `Offer = Trial` ;
  - l'e-mail de l'administrateur comme e-mail racine.
- Règles de l'adresse (`TrialSlug`, une seule définition partagée avec l'API) :
  - 3 à 30 caractères, `a-z`, `0-9` et `-`, sans tiret au début ni à la fin ;
  - une liste de noms réservés (`www`, `api`, `admin`, `app`, `mail`, `crm`, `support`, `status`, `jobs`, `demo`, `test`, `essai`…) ;
  - unique sur tout le registre.

### Lot 2 — Inscription publique dans l'API (3 j)

**Livré le 2026-11-11 (non commité).**
- **Code.** Le dossier `Sankore.Api/Features/TrialSignups/` contient :
  - les routes `/api/v1/public/trial-signups` : `GET slug-availability`, `POST /`, `POST confirm` ;
  - le contrôle Turnstile ;
  - le client du registre ;
  - la tâche `TrialSignupDecisionsJob`, qui tourne chaque minute.
- **Lien du mot de passe.** `IRootPasswordSetup` (Administration) fournit un lien d'activation de 7 jours pour le compte racine. La page d'activation existante l'accepte pour ce seul compte, tant qu'il ne s'est jamais connecté.
- **Modèles d'e-mail** (`NotificationsSeeder.TrialSignups`, fr et en sauf l'alerte à l'équipe) : `trial.signup-confirm`, `trial.signup-existing`, `trial.signup-received`, `trial.signup-rejected`, `trial.ready`.
- **Registre.** La langue de la demande est enregistrée (migration `AddTenantSignupLocale`), pour que les e-mails suivent la langue de la page du site.
- **Envoi.** Les e-mails sans institution partent sous l'identifiant `Guid.Empty`, donc par le relais de la plateforme, sans quota.
- **Limites de débit :** `trial-signup`, 5 demandes par heure et par IP ; `trial-lookup`, 30 par minute. Toutes deux comptent par l'IP que donne `CF-Connecting-IP`.
- **Vérifié en local :**
  - « Essai Public » : demande, e-mail de confirmation, et même réponse publique pour une adresse déjà utilisée, qui reçoit l'e-mail « une demande existe déjà ».
  - Confirmation : une seule alerte à l'équipe malgré deux clics.
  - Approbation : données de démarrage, puis e-mail « Votre CRM est prêt ».
  - Mot de passe choisi par le lien ; le lien refuse un second usage. La connexion fonctionne.
  - « Recette Essai » (lot 1) a reçu son e-mail « prêt » au même passage.
- **Reste pour mettre en ligne :** la page de confirmation du site (lot 4) et l'écran de validation (lot 3). D'ici là, l'approbation se fait par l'API du registre.

**Déplacé dans le registre le 2026-11-12 (propriétaire).** Le formulaire est désormais décidé par
`Sankore.Admin` (`Features/TenantSignups/PublicSide.cs`, routes `tenant-signups/public/slug-availability`,
`/submit`, `/confirm`, clé de service) : Turnstile, jeton de confirmation, règles de réponse au public et
choix des e-mails. `Sankore.Api/Features/TrialSignups/` ne garde que la porte publique — les trois routes
`/api/v1/public/trial-signups`, leurs limites de débit et l'IP du visiteur — et un relais (`TrialSignupRelay`)
qui renvoie la réponse du registre telle quelle et met en file, sous `Guid.Empty`, les e-mails qu'elle liste
(le registre n'a pas d'envoi). La tâche `TrialSignupDecisionsJob` reste dans l'API : l'e-mail « prêt » porte
le lien du compte racine, émis par Administration. Réglages déplacés dans la section `TrialSignup` du
registre : `ConfirmationUrl`, `ConfirmationUrlEn`, `Turnstile:SecretKey`, `PlatformRecipients` ; l'API ne
garde que `RequestsPerHourPerIp`. Le registre reste fermé au public.

Les routes publiques sont sur `Sankore.Api`, qui a déjà une surface publique (`/api/v1/public`, hors résolution d'institution) et sait envoyer des e-mails (M08). Ce n'est pas le registre : la clé du registre administre toutes les institutions, il ne doit pas être exposé aux anonymes.

- `GET /api/v1/public/trial-signups/slug-availability?slug=` : libre, pris ou réservé. Limite de débit stricte, car la route permet de deviner qui est client.
- `POST /api/v1/public/trial-signups` :
  - vérifie le jeton **Cloudflare Turnstile** côté serveur ;
  - limite à 5 demandes par heure et par IP (vraie IP lue derrière Cloudflare et Traefik) ;
  - valide les champs en renvoyant les messages en français ;
  - crée le jeton de confirmation : 32 octets aléatoires, dont seule l'empreinte va au registre ;
  - envoie l'e-mail « Confirmez votre adresse » avec le modèle plateforme `trial.signup.confirm` (fr/en).
  - Elle répond toujours la même chose, que l'e-mail soit déjà connu ou non.
- `POST /api/v1/public/trial-signups/confirm` (corps : le jeton) : passe la demande en `Confirmed`. Ensuite :
  - si D3 = validation, l'équipe est prévenue (D8) ;
  - si D3 = automatique, la demande est approuvée par la clé de service.
- Lien de confirmation : `https://talisoft.net/essai/confirmation#t=…`. Le jeton est dans le fragment, qui n'apparaît ni dans les journaux ni dans le `Referer`, comme pour le KYC à distance.
- **E-mail « Votre CRM est prêt »** : à la fin du premier chargement `Starter` d'une institution issue d'une demande, `TenantDataLoadJob` envoie l'adresse du CRM et un lien pour choisir le mot de passe. Ce lien vient d'une méthode de façade d'Administration qui émet le même jeton que « Mot de passe oublié », avec une durée de 7 jours comme l'activation. Clé d'idempotence par institution.
- CORS : ajouter `https://talisoft.net` aux origines de l'API. Le formulaire du site appelle l'API depuis le navigateur, ce qui garde la vraie IP pour la limite de débit.

### Lot 3 — File « Demandes d'essai » dans l'app plateforme (2 j)

**Livré le 2026-11-11 (non commité).**
- **App plateforme.** Le menu « Demandes d'essai » mène à `/essais` :
  - une file `tas-table` avec cinq onglets et leurs compteurs (À valider, E-mail à confirmer, Approuvées, Refusées, Expirées), une recherche différée de 300 ms et l'état gardé dans l'URL ;
  - une fiche `/essais/:id` : le demandeur, la demande (IP, navigateur), les demandes liées par IP et par domaine d'e-mail, l'approbation après confirmation et le refus avec motif dans un tiroir.
- **Fiche institution.** Elle affiche le badge « Essai gratuit » et l'offre. « Prolonger l'essai » ouvre un tiroir de date, et la route `PUT tenants/{id}/trial-end` est réservée à l'administrateur plateforme (refus pour une institution payante ou une date passée).
- **Client.** Le client du registre a été régénéré (`swaggers/sankore-crm-admin-api-swagger.json`, puis `npm run openapi-generator-admin`).
- **Vérifié dans Chrome :**
  - la file ;
  - « Coopérative Refus » refusée, avec l'e-mail du motif ;
  - « Mutuelle Écran » approuvée : l'institution est créée et l'e-mail « Votre CRM est prêt » est parti ;
  - l'essai prolongé au 31 décembre.
- **Vu en passant :**
  - les dates de l'app plateforme sont en anglais (« November »), comme sur tous ses écrans : la langue de l'app n'est pas réglée ;
  - la section Modules d'une institution en essai propose encore « Activer directement ». Le lot 6 doit le fermer.

- Menu « Demandes d'essai » : liste en `tas-table` (statut, institution, adresse, e-mail, pays, date), filtre par statut, compteur des demandes confirmées en attente.
- Détail : tous les champs, IP, autres demandes depuis la même IP ou le même domaine d'e-mail, puis « Approuver » ou « Refuser » avec un motif. Un refus envoie un e-mail sobre.
- Fiche institution : offre (Essai ou Payant) et date de fin d'essai, modifiable par la plateforme (prolonger un essai).

### Lot 4 — Formulaire du site Talisoft (1 à 1,5 j)

**Livré le 2026-11-11 (non commité), dans `talisoft-landing`.**
- **Pages** : `/essai-gratuit` et `/en/free-trial` (formulaire, étapes, conditions de l'essai en `#conditions`) ; `/essai/confirmation` et `/en/trial/confirmation` (`noindex`, hors sitemap). Code dans `src/app/trial/`.
- **Formulaire** (signal forms) : les champs de D7. L'adresse est déduite du nom tant que le visiteur ne la change pas, nettoyée pendant la frappe (« Ma Mutuelle » → `ma-mutuelle`) et vérifiée 400 ms après la dernière frappe (libre, prise, réservée). Les erreurs de l'API s'affichent sous chaque champ ; en anglais, la page met ses propres messages. Après l'envoi, toujours « Vérifiez votre boîte e-mail » (même réponse pour une adresse déjà connue).
- **Turnstile** : widget chargé sur la seule page d'essai, jeton redemandé après chaque envoi refusé.
- **Configuration** : le navigateur appelle l'API directement. Son adresse et la clé Turnstile viennent de `GET /api/trial/config`, servi par le serveur du site à partir de `CRM_API_URL`, `TURNSTILE_SITE_KEY` et `TRIAL_ADDRESS_SUFFIX` (`.env.example`, `docker-compose.yml`). Sans `CRM_API_URL` en production, la page affiche « L'essai gratuit ouvre bientôt » et renvoie au contact.
- **Confirmation** : la page lit `#t=…`, le retire aussitôt de la barre d'adresse, appelle la confirmation ; états confirmé, lien invalide, expiré, déjà traité, indisponible.
- **Boutons** : « Essai gratuit 30 jours » dans le bandeau d'accueil, bandeau « Envie de voir le CRM d'abord ? » sous l'offre pilote (accueil et page CRM).
- **Confidentialité** : sections « Votre demande d'essai gratuit » et « Vérification anti-robot (Cloudflare Turnstile) », fr et en. Durée de conservation des demandes sans suite à fixer (`LEGAL_INFO.trialRequestRetention`, entre crochets).
- **Vérifié en local** (site sur `ng serve`, API https://localhost:5080, maildev) : adresse déduite et libre, `mutuelle-ecran` prise, `admin` réservée, erreurs du formulaire vide ; envoi → un seul e-mail de confirmation ; lien → « Adresse confirmée », jeton retiré de l'adresse, alerte « Demande d'essai : Mutuelle Épargne Site » à l'équipe ; lien abîmé → « Ce lien n'est pas valable ». Tests : 62 (dont `trial-signup.spec.ts`), build et pré-rendu OK.
- **Reste avant la mise en ligne** : clé Turnstile (site + secret), `CRM_API_URL`, `https://talisoft.net` dans les origines CORS de l'API, relecture juridique des conditions et de la page Confidentialité (et sa contradiction avec AdSense et GTM).

Dans `talisoft-landing` :

- page « Essayer gratuitement » (fr et `/en/…`) ;
- formulaire des champs de D7, avec l'adresse vérifiée pendant la saisie (`acme` → `acme.<suffixe>` libre ou non), Turnstile et la case des conditions ;
- page `/essai/confirmation` : lit le fragment, appelle la confirmation, affiche « Adresse confirmée, votre CRM sera prêt sous peu » (ou « dès validation de votre demande » si D3 = validation) ;
- boutons « Essai gratuit » sur l'accueil et la page CRM, à côté de l'offre pilote.

À régler avant la mise en ligne (voir les points ouverts du site) : conditions de l'essai, mentions légales et page Confidentialité. Cette dernière doit parler du formulaire et de Turnstile, et sa contradiction actuelle avec AdSense et GTM est à trancher.

### Lot 5 — Fin d'essai et passage à l'offre payante (3 j)

- `TenantResolutionMiddleware`, quand `Offer = Trial` et `TrialExpiresAt` est passée :
  - toute requête autre que `GET` ou `HEAD` est refusée en 403 avec le code `TENANT_TRIAL_EXPIRED` ;
  - restent permis : connexion, rafraîchissement du jeton, déconnexion et routes d'export ;
  - les envois sortants (e-mails, webhooks, tâches de fond) s'arrêtent.
  - Le champ `TrialExpiresAt` est déjà dans `TenantInfo`, mais rien ne le lit aujourd'hui.
- Front CRM :
  - bandeau « Essai gratuit : X jours restants » à partir de J-14 (`TrialExpiresAt` est déjà dans le contexte de l'institution) ;
  - après J0, bandeau « Essai terminé — consultation seule » et lien « Passer à l'offre complète » ;
  - l'intercepteur d'erreurs reconnaît `TENANT_TRIAL_EXPIRED` et l'explique une fois.
- Tâche quotidienne (API), selon D4 et D5 :
  - envoie les relances de D5 ;
  - à J+30, suspend l'institution (`IsActive = false`, page « institution suspendue » existante) ;
  - à J+90, l'efface : vidage existant, puis suppression de l'institution.
  - Chaque étape est idempotente par institution et par date, pour qu'une tâche relancée n'envoie rien deux fois.
- **Passer à l'offre payante** (app plateforme) :
  - `Offer = Paid` et `TrialExpiresAt` vidée ;
  - **l'adresse ne change pas** (D2) : les liens déjà envoyés, les favoris et les sessions ouvertes continuent de fonctionner ;
  - un domaine propre au client reste possible plus tard, sur demande, en adresse supplémentaire (les adresses multiples existent déjà) ;
  - les données ne bougent pas ;
  - les modules peuvent alors être demandés et accordés.

### Lot 6 — Garde-fous de l'essai (2 à 3 j)

- E-mails : quota mensuel existant (`TryConsumeEmailQuotaAsync`), réglé à la valeur de D6 pour une institution `Trial`.
- SMS et WhatsApp : vérifier ce qui passe par des comptes de la plateforme et le fermer en essai (code `TRIAL_CHANNEL_UNAVAILABLE`).
- Utilisateurs : refus au-delà de la limite de D6 (`TRIAL_USER_LIMIT`), import compris.
- Modules : `module-subscriptions` refuse une demande d'une institution `Trial` (`TRIAL_MODULES_UNAVAILABLE`) ; la page Modules affiche l'invitation à l'offre complète.
- Domaines : pas d'ajout de domaine pour une institution `Trial`.
- Après ce lot, D3 peut passer en automatique.

### Lot 7 — Tâches de fond à l'échelle (3 à 5 j, avant une centaine d'institutions)

`TenantRecurringJobs.Register` inscrit 28 tâches récurrentes par institution, plus celles du jumeau « Mode test ». 500 essais feraient plus de 14 000 tâches planifiées dans la base. Il faut remplacer chaque famille par une tâche globale qui parcourt les institutions actives, avec la même cadence et la même tâche par institution. On sautera les institutions en lecture seule ou suspendues.

### Lot 8 — Recette de bout en bout (1 j)

- **Parcours complet** : formulaire, e-mail de confirmation, validation, institution créée, e-mail « Votre CRM est prêt », mot de passe choisi, connexion, données de démarrage socle présentes, aucune notion finance.
- **Fin d'essai** : écriture refusée, export permis, bandeau affiché.
- **Passage au payant** : écriture rétablie, module accordable.
- **Abus** :
  - adresse réservée refusée ;
  - adresse prise refusée ;
  - sixième demande de la même IP refusée ;
  - jeton expiré refusé ;
  - Turnstile absent refusé.

## 5. Sécurité

| Risque | Parade |
|---|---|
| Inscriptions de robots | Turnstile vérifié côté serveur, limite par IP, confirmation de l'e-mail. |
| Inscription au nom d'un tiers | Confirmation de l'e-mail avant toute création. |
| Hameçonnage sur notre domaine (« fausse banque ») | Validation par la plateforme au lancement (D3), noms réservés, refus avec motif, suspension en un clic. |
| Abus des envois (réputation du relais SMTP) | Quotas de l'essai, pas de SMS ni de WhatsApp par la plateforme, arrêt des envois à la fin de l'essai. |
| Deviner les clients par la vérification d'adresse | Limite de débit stricte ; la route ne dit rien d'autre que libre, pris ou réservé. |
| Exposition du registre | Les routes publiques sont dans l'API ; le registre ne reçoit que la clé de service. |
| Clés d'infrastructure | Aucun appel aux API Cloudflare ou Dokploy depuis le parcours d'inscription. |
| Jeton de confirmation | Seule l'empreinte est stockée ; jeton dans le fragment de l'URL ; 48 h ; usage unique. |
| Origines CORS | Suffixe d'un seul niveau, `https` seulement ; tests des cas limites. |
| Coexistence des essais et des clients payants dans une même base | Isolation existante par `tenant_id` (filtres de toutes les tables). Si un client payant exige une base séparée par contrat, c'est lui qu'on installe sur une instance dédiée, pas l'essai. |

## 6. Charge

| Lot | Charge |
|---|---|
| 0 — Adresse générique | 1 à 1,5 j |
| 1 — Demandes dans le registre | 2 j |
| 2 — Inscription publique (API) | 3 j |
| 3 — File dans l'app plateforme | 2 j |
| 4 — Formulaire du site | 1 à 1,5 j |
| 5 — Fin d'essai et passage au payant | 3 j |
| 6 — Garde-fous | 2 à 3 j |
| 7 — Tâches de fond à l'échelle | 3 à 5 j |
| 8 — Recette | 1 j |
| **Total** | **18 à 22 j** |

Ordre conseillé : 0, 1, 2, 3, 4. Avec la validation par la plateforme, l'essai peut alors ouvrir. Le lot 5 doit être livré **avant la fin du premier essai**, soit 3 mois après l'ouverture. Le lot 6 vient avant de passer en automatique, le lot 7 avant une centaine d'institutions.
