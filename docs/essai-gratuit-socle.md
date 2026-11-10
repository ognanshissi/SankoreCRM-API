# Essai gratuit du CRM socle

**Statut : plan rédigé le 2026-11-10. Domaine choisi : `talisoftcrm.com`. Lot 0 : partie code livrée le 2026-11-10 (non commitée), réglages Cloudflare et Traefik à faire. Décisions D2 à D8 en attente.**

## 1. Le besoin

Proposer le CRM socle (aucun module payant) à tout le monde, gratuitement, pendant 3 mois. Un visiteur du site Talisoft remplit un formulaire et reçoit, quelques minutes plus tard, l'adresse de son CRM et un lien pour choisir son mot de passe. Les fonctions plus poussées (Banking - Microfinance, Assurance) restent l'offre payante, sur un autre nom de domaine ou sur le domaine du client.

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
| D2 | Domaine de l'offre payante | Le domaine du client quand il en a un. Sinon `*.talisoft.net` (ou un autre suffixe), même mécanique que l'essai. |
| D3 | Validation des inscriptions | **Par la plateforme au lancement** : un clic « Approuver » dans l'app plateforme, le reste est automatique. On passe à la création automatique (réglage `TrialSignup:AutoApprove`) une fois les garde-fous du lot 6 en place. |
| D4 | Ce qui arrive à la fin de l'essai | J0 : **lecture seule** (consultation et export possibles, aucune écriture). J+30 : institution suspendue. J+90 : données effacées puis institution supprimée. Un e-mail est envoyé à chaque étape. |
| D5 | Rappels avant la fin | J-14, J-3, J0. |
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

- Menu « Demandes d'essai » : liste en `tas-table` (statut, institution, adresse, e-mail, pays, date), filtre par statut, compteur des demandes confirmées en attente.
- Détail : tous les champs, IP, autres demandes depuis la même IP ou le même domaine d'e-mail, puis « Approuver » ou « Refuser » avec un motif. Un refus envoie un e-mail sobre.
- Fiche institution : offre (Essai ou Payant) et date de fin d'essai, modifiable par la plateforme (prolonger un essai).

### Lot 4 — Formulaire du site Talisoft (1 à 1,5 j)

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
- Tâche quotidienne (API) : rappels de D5. Selon D4, elle suspend l'institution (`IsActive = false`, page « institution suspendue » existante) puis l'efface : vidage existant, puis suppression de l'institution, avec un e-mail 7 jours avant l'effacement.
- **Passer à l'offre payante** (app plateforme) :
  - `Offer = Paid` et `TrialExpiresAt` vidée ;
  - si le client a un domaine, ajout d'une adresse principale (les adresses multiples existent déjà) ;
  - l'ancienne adresse d'essai reste reconnue, pour que les liens déjà envoyés fonctionnent ;
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
