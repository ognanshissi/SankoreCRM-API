# US-M08-FE-01 — Écran de paramétrage des notifications

| | |
|---|---|
| **Rôle** | Administrateur d'IMF |
| **Module** | M08 Notifications (écran hébergé dans `packages/crm/settings`) |
| **Priorité** | MVP |
| **Estimation** | 8 |
| **Dépend de** | US-M08-BE — fournisseur par tenant, coffre à secrets, envoi de test, quota mensuel *(livré)* |

En tant qu'**administrateur d'IMF**, je veux configurer moi-même par quel fournisseur mes e-mails
partent, éprouver ce paramétrage avant de compter dessus, et plafonner ma consommation
mensuelle, **afin de** ne pas dépendre d'une intervention technique pour envoyer du courrier à
mes clients — et de ne pas découvrir une erreur de configuration à travers des messages jamais
reçus.

---

## Prérequis technique — à faire en premier

Le contrat a changé côté serveur. Le client généré doit être régénéré **avant** d'écrire
l'écran, sinon les champs `hasCredential`, `smtpHost`, `smtpPort`, `smtpUsername`,
`smtpUseSsl`, `smtpUseStartTls` et l'endpoint `test-send` n'existent pas dans
`@sankore/crm-api` :

1. lancer l'API, exporter le document OpenAPI et remplacer `swaggers/sankore-crm-api-swagger.json` ;
2. `npm run openapi-generator` ;
3. ne **jamais** éditer `packages/crm-api/src/lib` à la main.

Vérifier après régénération que `providerType` sort bien en **chaîne** (et non en entier) : le
générateur est en `stringEnums: true`, mais un enum déclaré `type: integer` dans le swagger sort
numérique, et `tas-select` travaille en chaînes. Si l'écart existe, convertir dans les deux sens
plutôt que de poser un `as any`.

## API consommée

| Méthode | Route | Permission |
|---|---|---|
| `GET` | `/api/v1/notification-settings` | `notification:settings:read` |
| `PUT` | `/api/v1/notification-settings` | `notification:settings:manage` |
| `PATCH` | `/api/v1/notification-settings/quota` | `notification:settings:quota` |
| `POST` | `/api/v1/notification-settings/test-send` | `notification:settings:manage` |

---

## Critères d'acceptation

### Consultation

1. Étant donné la permission `notification:settings:read`, quand j'ouvre
   `/settings/notifications`, alors je vois le fournisseur actif, l'adresse et le nom
   d'expédition, l'adresse de réponse, et — pour un relais SMTP — l'hôte, le port, l'utilisateur
   et le mode TLS.
2. Étant donné un tenant qui n'a jamais rien configuré, quand j'ouvre l'écran, alors le
   fournisseur affiché est « Compte de la plateforme » et aucun champ de relais n'est demandé.
3. Étant donné que je n'ai pas `notification:settings:manage`, quand j'ouvre l'écran, alors je
   consulte sans pouvoir modifier : les champs sont désactivés et les boutons
   « Enregistrer » et « Envoyer un test » absents.

### Identifiant — le point le plus sensible de l'écran

4. Étant donné un identifiant déjà enregistré, quand j'ouvre l'écran, alors le champ mot de
   passe / clé d'API est **vide** et porte la mention « Un identifiant est enregistré — laissez
   vide pour le conserver ». **Le serveur ne renvoie jamais le secret**, seulement
   `hasCredential: true` : afficher des points de suspension factices ferait croire à une valeur
   récupérable.
5. Étant donné que je modifie seulement l'adresse d'expédition, quand j'enregistre sans toucher
   au champ identifiant, alors l'identifiant stocké est conservé — aucune ressaisie exigée d'un
   administrateur qui ne connaît peut-être pas le mot de passe.
6. Étant donné que je change de fournisseur sans fournir d'identifiant, quand l'écran se
   recharge, alors `hasCredential` vaut `false` et un message indique qu'un identifiant est
   attendu pour ce fournisseur : le secret stocké appartenait au fournisseur précédent.

### Choix du fournisseur

7. Étant donné la liste des fournisseurs, quand je l'ouvre, alors elle propose : Compte de la
   plateforme (`Default`), SMTP (`Smtp`), Brevo (`Brevo`), Amazon SES (`Ses`), Postmark
   (`Postmark`), SendGrid (`SendGrid`).
8. Étant donné que je choisis `Smtp`, quand le formulaire se met à jour, alors les champs hôte
   (obligatoire), port, utilisateur, et le mode TLS apparaissent.
9. Étant donné que je laisse le port vide, quand j'enregistre, alors le serveur le déduit du
   mode TLS (465 en TLS implicite, 587 en STARTTLS) : l'aide du champ le dit, plutôt que
   d'imposer à l'utilisateur de s'en souvenir.
10. Étant donné les deux bascules TLS, quand j'active les deux, alors le formulaire est invalide
    avec un message explicite — elles sont **mutuellement exclusives**.
11. Étant donné que je choisis `Postmark`, quand le formulaire se met à jour, alors le domaine
    d'envoi devient obligatoire.
12. Étant donné que je choisis `Ses`, `Postmark` ou `SendGrid`, quand le formulaire se met à
    jour, alors un encart signale que **ces fournisseurs ne sont pas encore implémentés côté
    serveur : les messages seront journalisés sans être envoyés**. Sans cet avertissement, un
    opérateur croit son courrier parti.
13. Étant donné `Brevo`, quand le formulaire se met à jour, alors un seul identifiant est
    demandé — la clé d'API — et aucun champ SMTP.

### Envoi de test

14. Étant donné un paramétrage enregistré, quand je clique « Envoyer un test » et saisis une
    adresse, alors un message part immédiatement par le fournisseur configuré.
15. Étant donné que le fournisseur refuse le message, quand la réponse arrive, alors **le refus
    du fournisseur est affiché tel quel**. Attention : l'endpoint répond **HTTP 200** avec
    `{ delivered: false, error: "…" }` — un code 2xx ne prouve rien ici, c'est `delivered` qui
    tranche.
16. Étant donné un test réussi, quand la réponse arrive, alors le message de succès précise si
    c'est le relais du tenant ou le compte de la plateforme qui a servi (`usedTenantProvider`).
17. Étant donné que je viens de modifier le formulaire sans enregistrer, quand je clique
    « Envoyer un test », alors je suis averti que le test porte sur le paramétrage **enregistré**
    et non sur ma saisie en cours.

### Quota mensuel

18. Étant donné un quota défini, quand j'ouvre l'écran, alors je vois la consommation du mois en
    cours et le plafond, en `tabular-nums`.
19. Étant donné un champ quota laissé vide, quand j'enregistre, alors le tenant est illimité — et
    l'écran précise que la consommation reste comptée, ce qui permet de choisir un plafond en
    connaissance de cause.
20. Étant donné le quota, quand je l'enregistre, alors il passe par `PATCH .../quota`,
    indépendamment du reste du formulaire : c'est une permission distincte
    (`notification:settings:quota`).
21. Étant donné que le plafond est atteint, quand j'ouvre l'écran, alors un encart indique que
    **les messages suivants sont rejetés définitivement, sans nouvelle tentative**, jusqu'au
    changement de mois.

### Erreurs

22. Étant donné une erreur de validation serveur, quand elle revient, alors les messages de
    `err.error?.errors` sont affichés champ par champ — pas un message générique qui masque
    l'information utile.
23. Étant donné une panne réseau, quand elle survient, alors un toast d'erreur s'affiche et le
    formulaire conserve la saisie.

---

## Contraintes de mise en œuvre

Conventions du workspace à respecter — elles ne sont pas négociables, ce sont des décisions déjà
prises :

- **Signal forms obligatoires** (`@angular/forms/signals`) : modèle en classe avec fabrique
  statique (`NotificationSettingsFormModel.fromDto(dto)`), `form()`, `submit()`. Pas de
  `[(ngModel)]`, pas de Reactive Forms.
- **Pas de liste sur cet écran** : c'est un formulaire. La règle `tas-table` ne s'applique pas
  ici — ne pas en introduire une pour afficher deux ou trois valeurs de synthèse.
- **`SnackbarService` n'a pas de `warning`** : pour les avertissements des critères 12, 17 et 21,
  utiliser `info`, ou mieux un encart persistant dans la page (un toast disparaît, l'information
  doit rester visible).
- **Permissions via `PermissionsService`** : un champ par droit en tête de classe
  (`canManage = this._permissions.can('notification:settings:manage')`), lu comme signal dans le
  template. Pas de `computed` artisanal sur `connectedUser()`.
- **Route** dans `packages/crm/settings/src/settings.routes.ts`, en `loadComponent` + `import()`,
  protégée par `hasPermissionGuard('notification:settings:read')`. Composant en
  `export default NotificationSettingsPage;`.
- Fil d'Ariane posé dans `ngOnInit` via `BreadcrumbService.set([...])`.
- Textes d'interface **en français**, y compris erreurs et placeholders.
- Composants `@talisoft/ui` plutôt que du HTML brut : `tas-card`, `tas-form-field`, `tas-input`,
  `tas-select`, `tas-tag` pour l'état du quota, `tas-spinner`, `SideDrawerService` si le test
  d'envoi se fait dans un drawer.
- Gestion d'erreur à l'idiome du repo : `catchError` → snackbar → `return EMPTY`, avec
  `HttpErrorResponse` typé quand le diagnostic compte.
- **Ne pas envoyer `useDefaultPlatformProvider`** : le serveur le déduit du fournisseur choisi.

## Definition of Done

- `npx tsc --noEmit -p packages/crm/settings/tsconfig.lib.json` sans erreur nouvelle.
- Écran vérifié au `npm start` : `tsc` ne contrôle pas les templates Angular, une erreur de
  binding ou un composant oublié dans `imports` ne sort qu'au serve.
- Les quatre parcours éprouvés à la main : enregistrement d'un relais SMTP avec mot de passe,
  modification de l'expéditeur **sans** ressaisie du mot de passe, bascule vers Brevo, et test
  d'envoi en échec avec affichage du refus du fournisseur.
- Aucun fichier de `packages/crm-api/src/lib` modifié à la main.
- Le secret n'apparaît ni dans l'URL, ni dans un log de console, ni dans le state persisté.
