# Guide d'intégration des formulaires

Ce guide explique comment envoyer les demandes d'un formulaire de site web dans Sankore CRM. Il s'adresse au **webmaster** qui intègre le formulaire et à l'**administrateur CRM** qui prépare la source.

Chaque demande envoyée devient un **lead** dans le CRM, attribué à un agent selon les règles de dispatching de l'institution.

## 1. Choisir sa méthode

| | A. Formulaire créé dans le CRM | B. Votre propre formulaire |
|---|---|---|
| Pour qui | Un site qui veut un formulaire simple, sans développement | Un site qui a son propre design ou un parcours sur mesure |
| Où se règlent les champs | Dans le CRM (onglet « Champs du formulaire ») | Dans votre code ; le CRM dit quels champs il attend |
| Ce que le site intègre | Une balise `<script>` et un conteneur | Le code fourni par le CRM (Angular, React) ou le script en mode « attaché » (HTML) |
| Modifier un champ ou un texte | Dans le CRM : en ligne en 5 minutes au plus, sans redéployer | Dans le code du site, puis redéploiement |
| Captcha | Affiché par le formulaire | À intégrer dans votre formulaire |

**Recommandation :** commencez par la méthode A. Passez à la méthode B si le formulaire doit suivre le design du site au pixel près, ou s'il fait partie d'un parcours (étapes, calculs, envoi vers un autre système en même temps).

## 2. Préparer la source dans le CRM (administrateur)

Dans **Paramétrage → Sources & Campagnes**, créez une source de type **« Script embarqué »**, puis :

1. **Onglet « Formulaire », origines autorisées :** l'adresse exacte du site, schéma compris, sans chemin, par exemple `https://www.mon-institution.ci`.
   - Une adresse par domaine : `https://mon-institution.ci` et `https://www.mon-institution.ci` sont deux origines.
   - Pour tester sur un poste de développeur, une source de test (clé `pk_test_…`) accepte aussi `http://localhost:4200`. Ces adresses locales ne sont jamais copiées en production.
2. **Onglet « Formulaire », mode du formulaire :** « Formulaire hébergé » pour la méthode A, « Formulaire existant » pour la méthode B.
3. **Captcha (conseillé) :** choisissez le service (Cloudflare Turnstile, hCaptcha ou reCAPTCHA v3) et saisissez sa **clé du site**. Enregistrez ensuite sa **clé secrète** dans le bloc « Captcha » qui apparaît sous ce même onglet. Sans clé secrète, la source ne peut pas être activée.
4. **Méthode A :** composez le formulaire dans l'onglet « Champs du formulaire ». Le texte de consentement y est obligatoire.
5. **Méthode B :** déclarez les champs que votre formulaire envoie dans l'onglet « Correspondance des champs » (voir § 5).
6. **Activez la source.** Une source en pause ou en brouillon refuse les envois. Une source « Test » les accepte ; les leads d'une clé `pk_test_…` arrivent dans le mode test.

L'onglet **« Installer le script »** donne ensuite le code à intégrer, prêt à copier, avec la clé publique de la source et l'adresse de l'API déjà renseignées. Le bouton « Envoyer au webmaster » l'envoie par e-mail.

## 3. Méthode A — le formulaire créé dans le CRM

### Site HTML, WordPress, Wix, Webflow

Collez le code de l'onglet « Installer le script » (guide « HTML ») à l'endroit où le formulaire doit apparaître :

```html
<div id="sankore-form"></div>
<script src="https://api…/sdk/1.4.0/forms.min.js"
        integrity="sha384-…" crossorigin="anonymous"
        data-sankore-key="pk_live_…"
        data-sankore-api="https://api…"
        data-sankore-render="true"
        data-sankore-container="#sankore-form"
        defer></script>
```

- **WordPress :** un bloc « HTML personnalisé » dans la page, ou une extension comme *Insert Headers and Footers* pour le script.
- **Wix :** Paramètres → Code personnalisé, « Corps — fin ».
- **Webflow :** Project Settings → Custom Code → Footer.

### Angular

1. Collez le script **une seule fois** dans `src/index.html`, avant `</body>` (guide « Angular » de l'onglet « Installer le script » : le code est donné sans le `<div>`).
2. Ajoutez le composant fourni, puis affichez `<app-sankore-form />` où le formulaire doit apparaître :

```ts
@Component({ selector: 'app-sankore-form', template: '<div id="sankore-form"></div>' })
export class SankoreForm {
  public readonly sent = output<string | null>();   // le CRM a reçu la demande
  public readonly failed = output<string | null>(); // le CRM l'a refusée
  // … (code complet dans le guide « Angular »)
}
```

Le script remplit le conteneur dès qu'il s'affiche, y compris après un changement de page ou dans un `@if`.

### React et Next.js

1. Collez le script une seule fois dans `index.html` (Vite) ou dans le `<head>` de la mise en page (Next.js).
2. Ajoutez le composant fourni (guide « React ») et affichez `<SankoreForm onSent={…} onError={…} />`. Avec Next.js, ajoutez `'use client';` en tête du fichier.

### Ce que fait le formulaire

- Il affiche les champs, la case de consentement (obligatoire), la case WhatsApp si elle est configurée, le captcha et le bouton.
- Après l'envoi, il affiche le message de la source, ou ouvre la page de redirection configurée.
- Son style suit la couleur et la police de la source. Le site peut les remplacer avec les variables CSS `--sankore-primary` et `--sankore-font`.

## 4. Méthode B — votre propre formulaire

### Angular ou React : le code fourni par le CRM

Le guide « Angular » (ou « React ») de l'onglet « Installer le script » donne un fichier à copier tel quel :

- **Angular :** `sankore-lead.service.ts`, un service à injecter.
- **React :** `useSankoreLead.ts`, un hook qui expose `submit`, `status` et `error`.

Le fichier contient l'interface **`SankoreLeadFields`** : la liste exacte des champs que la source attend, tirée de sa correspondance des champs. Un champ obligatoire oublié ou une clé mal orthographiée ne compile pas. **Si la correspondance change dans le CRM, recopiez le fichier.**

```ts
const result = await this._sankore.submit(
  { fullName: this.fullName, phone: this.phone },        // SankoreLeadFields
  { consent: this.consent, formLoadedAt: this._shownAt, captchaToken: this.token },
);
if (result.ok) { /* merci */ } else { /* result.error : voir § 8 */ }
```

- `consent` : la case de consentement cochée par le visiteur. Sans elle, le CRM refuse la demande.
- `formLoadedAt` : le moment où le formulaire est apparu. Un envoi plus rapide que le délai réglé sur la source (3 secondes par défaut) est écarté comme venant d'un robot.
- `captchaToken` : le jeton du widget captcha, si la source en a un. Un jeton ne sert qu'une fois : réinitialisez le widget après chaque envoi.
- Le code fonctionne avec le rendu côté serveur (SSR) : il n'envoie rien depuis le serveur.

### Site HTML : le script en mode « attaché »

Le script se branche sur votre `<form>` et envoie ses champs nommés :

```html
<form id="contact">
  <input name="fullName" required>
  <input name="phoneNumber" type="tel" required>
  <textarea name="comment"></textarea>
  <label><input type="checkbox" name="consent" required> J'accepte…</label>
  <button>Envoyer</button>
</form>
<script src="https://api…/sdk/1.4.0/forms.min.js" integrity="sha384-…" crossorigin="anonymous"
        data-sankore-key="pk_live_…" data-sankore-api="https://api…"
        data-sankore-form="#contact" defer></script>
```

- Une case nommée `whatsapp_consent` recueille l'accord WhatsApp (facultatif).
- Un widget Turnstile, hCaptcha ou reCAPTCHA placé dans le formulaire est lu automatiquement.
- Par défaut, le script remplace le formulaire par le message de la source. Avec `data-sankore-keep-form="true"`, il laisse le formulaire en place et ne fait qu'émettre les évènements du § 7 : la page affiche alors son propre message.

## 5. Les champs attendus

Deux possibilités :

- **Sans règle de correspondance**, le CRM lit les noms de champs ci-dessous tels quels.
- **Avec des règles** (onglet « Correspondance des champs »), chaque règle dit : « le site envoie `phone`, c'est le Téléphone ». Seules les règles sont alors appliquées. Les règles permettent aussi de formater le téléphone en E.164 avec un pays par défaut, et de convertir des valeurs.

| Nom dans le CRM | Contenu | |
|---|---|---|
| `phoneNumber` | Téléphone | obligatoire |
| `fullName`, `firstName`, `lastName` | Nom complet, ou prénom et nom | au moins un |
| `email` | E-mail | |
| `interestedProduct` | Produit d'intérêt (rapproché du catalogue) | |
| `comment` | Message du visiteur | |
| `preferredLanguage` | Langue (`fr`, `en`…) | |
| `companyName` | Entreprise : le lead devient un prospect « entreprise » | |
| `companyEmail`, `companyPhone`, `website`, `contactJobTitle` | Coordonnées de l'entreprise et fonction du contact | |
| `campaign`, `externalReference` | Campagne, référence externe | |
| `custom.<clé>` | Champ personnalisé du formulaire Lead de l'institution | |

Les paramètres UTM de la page d'arrivée, la page et la provenance sont ajoutés automatiquement par le script et par le code fourni.

## 6. Consentement, captcha et protections

- **Consentement :** chaque demande doit porter l'accord du visiteur (case cochée). Le texte de la case se règle dans le CRM pour la méthode A, et dans votre formulaire pour la méthode B.
- **Captcha :** il est vérifié par le CRM, avec la clé secrète de la source, et jamais par le navigateur. Le formulaire du CRM l'affiche lui-même. Dans votre formulaire, ajoutez le widget du service avec la **clé du site** de la source.
- **Limites :** 10 envois par minute et par visiteur pour une même source, et un délai minimal de saisie (3 secondes par défaut).
- **Origines :** une demande venant d'une adresse absente des origines autorisées est refusée.

## 7. Évènements (script 1.4.0)

Le script prévient la page de l'issue de chaque envoi. Les évènements sont émis sur le formulaire (méthode B en HTML) ou sur le conteneur (méthode A), et remontent dans la page.

| Évènement | `detail` | Quand |
|---|---|---|
| `sankore:success` | `{ ingestionId }` | Le CRM a reçu la demande |
| `sankore:error` | `{ status, error }` | Le CRM l'a refusée, ou le réseau a échoué (`status` 0) |

```js
document.querySelector('#sankore-form')
  .addEventListener('sankore:success', (e) => analytics.track('lead', e.detail));
```

## 8. Vérifier et dépanner

**Vérifier l'installation :** à la première visite d'une page qui porte le script ou le code fourni, l'onglet « Vérification » de la source affiche « Script détecté ». Chaque envoi apparaît dans l'onglet « Réceptions » de la source, avec son résultat.

| Symptôme | Cause probable | Solution |
|---|---|---|
| Réponse **403** | Origine absente des origines autorisées (attention à `http`/`https` et au `www`), ou source en pause | Corriger l'origine ; réactiver la source |
| Réponse **404** | Source en brouillon ou archivée, ou clé publique régénérée | Activer la source ; recopier le code de l'onglet « Installer le script » |
| `ConsentRequired` | La case de consentement n'est pas cochée, ou n'est pas envoyée | Vérifier `consent` (méthode B) ; le texte de consentement (méthode A) |
| `CaptchaFailed` | Jeton absent, expiré ou déjà utilisé | Attendre la fin de la vérification ; réinitialiser le widget après chaque envoi |
| `CaptchaNotConfigured` | Clé secrète du captcha absente de la source | La saisir dans le bloc « Captcha » de l'onglet « Formulaire » |
| Ingestion `REQUIRED_FIELDS_MISSING` | Téléphone ou nom absent après correspondance | Vérifier les noms envoyés ou les règles de correspondance |
| Erreur CORS ou réseau (`status` 0) | Adresse de l'API erronée, ou origine refusée | Recopier le code de l'onglet « Installer le script » ; vérifier l'origine |
| **429** | Plus de 10 envois par minute depuis le même poste | Attendre une minute (protection anti-abus) |

## 9. Référence du script

| Attribut | Rôle |
|---|---|
| `data-sankore-key` | Clé publique de la source (obligatoire) |
| `data-sankore-api` | Adresse de l'API du CRM |
| `data-sankore-render="true"` | Méthode A : afficher le formulaire du CRM |
| `data-sankore-container` | Méthode A : où l'afficher (sélecteur CSS) |
| `data-sankore-form` | Méthode B : le formulaire à brancher (sélecteur CSS) |
| `data-sankore-keep-form="true"` | Méthode B : ne pas remplacer le formulaire, seulement émettre les évènements |
| `data-sankore-success` | Méthode B : message affiché après l'envoi |
| `data-sankore-redirect` | Méthode B : page ouverte après l'envoi |
| `data-sankore-prevent="false"` | Méthode B : laisser le formulaire s'envoyer aussi vers sa propre adresse |

Chaque version du script est figée et vérifiée par son empreinte (`integrity`). Pour profiter d'une nouvelle version, recopiez le code de l'onglet « Installer le script ».
