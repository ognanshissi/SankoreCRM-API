# Formulaire hébergé et intégration des formulaires

**Statut : plan validé le 2026-11-14. Plan terminé : lots 1 à 5 livrés le 2026-11-14 (non commités).**

## 1. Le besoin

Une institution doit pouvoir mettre un formulaire de contact sur son site de deux façons, documentées clairement pour son webmaster :

- **le formulaire du site**, envoyé au CRM par le code à copier (onglets Angular et React de l'onglet « Script ») ou par le script en mode attaché ;
- **le formulaire créé dans le CRM** (« formulaire hébergé ») : deux lignes collées une fois sur le site, puis tout se règle dans le CRM.

## 2. Décisions du propriétaire (2026-11-14)

| Sujet | Décision |
|---|---|
| Code Angular/React | Du **code à copier** depuis l'onglet « Script », pas de paquet npm. |
| Champs attendus | **Configurés dans le CRM** : les règles de correspondance font foi, et le code généré les type (`SankoreLeadFields`). Sans règle, les noms du CRM. |
| Captcha | **Vérifié par le CRM** avec la clé secrète saisie sur la source (`captchaSecret`). |
| Formulaire hébergé | On corrige **tout** avant de le documenter : nouvelle version du script (1.4.0). |
| Documentation | Un guide en ligne à envoyer aux webmasters, et la même source en Markdown dans ce dossier. |

## 3. Défauts constatés du formulaire hébergé

1. **Champs perdus.** Le catalogue de l'éditeur propose `productCode`, `message` et `city`. L'ingestion lit `interestedProduct` et `comment` et n'a pas de champ ville : ces valeurs sont jetées sans erreur.
2. **Pas de captcha.** Le formulaire hébergé n'envoie jamais de jeton. Une source avec captcha refuse donc tous ses envois.
3. **Consentement piégeux.** Sans texte de consentement, aucune case ne s'affiche et chaque envoi est refusé (`ConsentRequired`).
4. **Message figé.** Le message de succès est écrit en dur en français. Le message et la redirection réglés sur la source sont ignorés.
5. **Intégration Angular/React.** Le script cherche son conteneur une seule fois, au chargement, et ne prévient la page ni du succès ni de l'échec. CaisseAvenir surveille la page avec un `MutationObserver` pour s'en apercevoir.

## 4. Lots

| Lot | Contenu | Statut |
|---|---|---|
| 1 | Catalogue aux noms du CRM (`interestedProduct`, `comment` ; la ville retirée). Les anciens noms des formulaires déjà enregistrés restent lus à l'ingestion (`productCode`, `message`, `city` vers le commentaire). | livré |
| 2 | Serveur : la définition du formulaire porte le captcha (fournisseur, clé du site), le message de succès et la redirection ; texte de consentement obligatoire en mode hébergé (éditeur et activation, `HostedConsentTextMissing`) ; activation refusée sans clé secrète du captcha (`CaptchaSecretMissing`). | livré |
| 3 | Script 1.4.0 : captcha dans le formulaire hébergé (Turnstile, hCaptcha, reCAPTCHA v3), message et redirection de la source, évènements `sankore:success` / `sankore:error` dans les deux modes ; le script surveille la page (formulaire ou conteneur affiché plus tard, recréé par une route) ; `data-sankore-keep-form`. À copier sur le volume `/data/sdk` en production. | livré |
| 4 | Onglet « Script » en mode hébergé : le script une seule fois dans `index.html`, et un composant Angular / React qui affiche le conteneur et écoute `sankore:success` / `sankore:error` (le script 1.4.0 remplit le conteneur dès qu'il apparaît). | livré |
| 5 | Le guide d'intégration : [guide-integration-formulaires.md](guide-integration-formulaires.md) et la page en ligne https://claude.ai/artifact/Mt9a2Sa8Fvvimz4BtQYeZE (privée tant qu'elle n'est pas partagée). | livré |
