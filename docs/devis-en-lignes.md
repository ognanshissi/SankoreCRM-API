# Devis en lignes

**Statut : plan validé le 2026-11-10. Plan terminé : lots 1 à 6 livrés le 2026-11-10 (non commités).**

## 1. Le besoin

Un tenant qui vend des produits livrables veut, sur un projet :

- ajouter **plusieurs produits**, chacun avec sa quantité, son prix unitaire, sa remise et son total ;
- obtenir un **devis en PDF** à son en-tête (logo, raison sociale, adresse, identifiants légaux), puis l'envoyer au prospect ou au client.

Aujourd'hui, un projet porte un seul produit (`Opportunity.Product`, `ProductLineId`) et un seul montant estimé. Les produits du catalogue n'ont pas de prix. Un devis ne peut être qu'un fichier fait ailleurs et déposé sur le projet.

La fonctionnalité appartient au **socle** (voir [isolation-socle.md](isolation-socle.md)) et prolonge [prospection-grands-comptes.md](prospection-grands-comptes.md) (§ 7, « devis en lignes »).

## 2. Décisions du propriétaire (2026-11-10)

| Sujet | Décision |
|---|---|
| Génération du devis | Le CRM **génère le devis en PDF** à l'en-tête de l'institution. Cela remplace le choix du 2026-11-08 (« documents déposés, pas générés ») pour les devis ; le dépôt d'un document fait ailleurs reste possible. |
| TVA | Taux réglés par l'institution et par produit (normal, réduit, exonéré), jamais codés en dur. Prix saisis hors taxes, TVA par ligne, totaux HT, TVA et TTC. |
| Devis ou facture | Le CRM produit des **devis** (ou proformas), jamais des factures. En Côte d'Ivoire, la facture passe par la facture normalisée électronique de la DGI, obligatoire depuis 2025. Elle reste dans le logiciel de facturation de l'entreprise, comme l'argent reste au core banking. |
| Prix et remise | Le prix vient du catalogue, et l'agent peut le modifier. La remise est plafonnée par l'institution. Au-delà, le devis attend la validation d'un responsable. |
| Produits finance | Les lignes multiples sont réservées aux produits **Standard** du socle. Un projet de crédit, d'épargne ou d'assurance reste à un seul produit, comme aujourd'hui. |

## 3. Ce qui existe déjà

- **Génération de PDF :** la bibliothèque QuestPDF est utilisée par `ProviderDocPdfGenerator`, qui produit la documentation fournisseur des sources de leads.
- **Institution :** `CompanyInfo` porte le nom, le logo, les couleurs et la monnaie.
- **Pièces d'un projet :** `ProjectAttachment`, dans un stockage chiffré.
- **Envoi :** une pièce du projet part par e-mail ou par WhatsApp (dans la fenêtre de 24 h, ou par modèle à en-tête document), et l'envoi est tracé dans l'historique du projet et l'onglet Documents du client ([[project-documents-sending-plan]]).
- **Validation :** les circuits de workflow d'un projet et la liste « Avant d'approuver ».
- **Montant estimé :** il alimente les conditions d'étape, les prévisions, les rapports et le potentiel grand compte.

## 4. Ce qui manque

| Manque | Conséquence |
|---|---|
| Aucun prix ni taux de TVA au catalogue | Rien à proposer par défaut sur une ligne |
| Pas d'identité légale de l'institution : raison sociale, forme juridique, adresse du siège, téléphone, e-mail, site, RCCM, n° de compte contribuable, régime d'imposition | Un devis ne peut pas porter l'en-tête attendu |
| Pas de lignes sur un projet | Un devis à plusieurs produits est impossible |
| Pas de numérotation, de validité ni de conditions de paiement | Un devis ne peut pas être référencé ni daté |

## 5. Lots

| Lot | Contenu | Résultat |
|---|---|---|
| **1. Prix et TVA au catalogue** | Prix unitaire HT, unité et taux de TVA sur les produits Standard ; taux de l'institution ; plafond de remise | Chaque produit vendable a son prix et sa TVA |
| **2. Identité légale de l'institution** | Raison sociale, adresse, identifiants légaux, pied de page ; numérotation, validité et conditions de paiement des devis | Un devis peut porter l'en-tête de l'institution |
| **3. Lignes de projet (serveur)** | Lignes, remises, totaux ; montant estimé = total HT ; remise au-delà du plafond = validation | Un projet porte plusieurs produits chiffrés |
| **4. Lignes de projet (écran)** | Saisie des lignes à la création et sur le projet ; totaux en direct | L'agent compose son offre |
| **5. Devis PDF** | Génération, numéro, versions ; rangé dans les pièces du projet ; envoi existant | Le prospect reçoit un devis à l'en-tête de l'institution |
| **6. Démo et recette** | Prix sur la démo socle ; un devis grand compte ; recette de bout en bout | La démo montre un devis complet |

### Lot 1. Prix et TVA au catalogue

**Sur un produit Standard :**
- prix unitaire HT, dans la monnaie de l'institution ;
- unité (pièce, carton, heure, mois…), en texte libre ;
- catégorie de TVA : normale, réduite ou exonérée.

**Réglages de l'institution :**
- le taux normal et le taux réduit. Ils sont préremplis à 18 % et 9 %, les taux de la Côte d'Ivoire, et toujours modifiables : ailleurs dans l'UEMOA, le taux normal se situe entre 15 % et 20 % ;
- le plafond de remise, en %.

Les produits des modules (crédit, épargne, assurance) ne reçoivent aucun de ces champs.

Une évolution de prix ou de taux ne modifie pas les lignes déjà saisies : chaque ligne garde le prix et le taux du moment de sa saisie.

**Livré le 2026-11-10 :**
- `ProductVatCategory` (Shared.Kernel : Standard, Reduced, Exempt), pour que le module Leads le lise au lot 3.
- `ProductSpeciality` : `UnitPrice` (HT, 2 décimales, null = sans prix), `PriceUnit` (30 caractères), `VatCategory`. `SetPricing` refuse un produit d'un module (`PRODUCT_PRICING_STANDARD_ONLY`) et un prix négatif.
- `PUT products/{id}/pricing` (`product:update`). Le prix est repris par la lecture des produits et par « Copier en production » / « Repartir de la production ».
- `CompanyInfo` : `VatStandardRate`, `VatReducedRate`, `MaxDiscountPercent` (null = pas de plafond). `GET company-info/pricing` est ouvert à tout rôle métier (`company-info:read`), car l'agent en aura besoin pour ses devis. `PUT company-info/pricing` demande `company-info:update`, et il est refusé en mode test comme le reste de l'identité de l'institution.
- Migration `AddPricingAndVat` : les produits existants passent en « Standard », les institutions existantes à 18 % et 9 %. Ces valeurs par défaut ne sont pas dans le modèle EF, qui sinon enregistrerait un taux de 0 % comme le taux par défaut.
- Écrans :
  - carte « Prix et TVA » dans l'onglet Paramètres d'un produit Standard ;
  - carte « TVA et remises » sous l'identité de l'institution, en lecture seule sans le droit de modifier.
- Recette API sur tenant-socle :
  - lecture des taux par un agent ;
  - prix enregistré et relu ;
  - refus d'un prix négatif, d'un taux réduit au-dessus du taux normal et de l'écriture par un agent (403).
  - Les données ont été remises en l'état après la recette.
- Contrôle visuel des deux cartes : pas fait.

### Lot 2. Identité légale de l'institution

**Nouveaux champs du réglage de l'institution :**
- raison sociale et forme juridique ;
- adresse du siège, téléphone, e-mail, site web ;
- RCCM, n° de compte contribuable, régime d'imposition ;
- texte libre de bas de devis.

**Réglages des devis :**
- préfixe et compteur annuel de numérotation (par exemple DEV-2026-000001) ;
- durée de validité par défaut, en jours ;
- conditions de paiement par défaut.

Les mentions obligatoires d'un devis ne sont pas imposées par le code. L'institution remplit ces champs et le bas de page, et la liste exacte est à faire confirmer par un expert-comptable (§ 6).

**Livré le 2026-11-10 :**
- `CompanyInfo` reçoit l'identité légale (`LegalName`, `LegalForm`, `HeadOfficeAddress`, `LegalPhone`, `LegalEmail`, `Website`, `Rccm`, `TaxpayerNumber`, `TaxRegime`, `QuoteFooter`). Tout est facultatif, et une raison sociale vide imprime le nom de l'organisation.
- Réglages des devis, aussi sur `CompanyInfo` :
  - `QuotePrefix` : DEV par défaut, 1 à 10 lettres, chiffres ou tirets ;
  - `QuoteValidityDays` : 30 par défaut, de 1 à 365 ;
  - `QuotePaymentTerms`.
- Routes, sous les règles du groupe `company-info` (lecture `company-info:read`, écriture `company-info:update`, refusée en mode test) :
  - `GET/PUT company-info/legal-identity` ;
  - `GET/PUT company-info/quote-settings`. La lecture donne `nextNumber`, le prochain numéro, sans le consommer.
- Numérotation :
  - table `administration.quote_number_counters`, une ligne par tenant et par année ;
  - prise du numéro par un seul upsert PostgreSQL (`INSERT … ON CONFLICT … RETURNING`), donc jamais deux fois le même numéro ;
  - le numéro est enregistré à part, hors de la transaction de l'appelant. Un numéro pris par un devis qui échoue laisse un trou, ce qui est admis pour un devis, pas pour une facture ;
  - le compteur est celui du tenant des données : le mode test numérote ses propres devis.
- `IQuoteIssuer` (Administration.PublicApi) est ce que le lot 5 lira pour imprimer le devis. Il donne l'identité, la monnaie, les taux, le plafond et les réglages, et `NextNumberAsync(tenant, date)`.
- Migration `AddQuoteIdentity` : DEV et 30 jours pour les institutions existantes.
- Écrans : cartes « Identité légale » et « Devis » sur la page Institution, chacune avec son bouton. L'aperçu du prochain numéro suit le préfixe saisi.
- Recette :
  - API sur tenant-socle : lecture par un agent, refus d'un site web, d'un e-mail ou d'un préfixe invalides et de l'écriture par un agent (403), enregistrement et relecture. Les données ont été remises en l'état ;
  - l'upsert du compteur, joué deux fois sur PostgreSQL dans une transaction annulée, a donné 1 puis 2.
- Contrôle visuel des cartes : pas fait.

### Lot 3. Lignes de projet (serveur)

**Une ligne :**
- produit du catalogue et libellé (modifiable) ;
- quantité et unité ;
- prix unitaire HT, repris du catalogue et modifiable ;
- remise en % ou en montant ;
- taux de TVA figé à la saisie ;
- totaux de la ligne : HT, TVA et TTC.

**Pied du projet :** une remise globale facultative, puis les totaux HT, TVA et TTC. La remise s'applique avant la TVA, principe à faire confirmer (§ 6).

**Montant estimé :** il devient le **total HT** dès qu'un projet a des lignes. Les conditions d'étape, les prévisions, les rapports et le potentiel grand compte continuent donc de fonctionner sans changement. Un projet sans ligne garde le montant saisi à la main.

**Produits autorisés :** seulement les produits Standard. Le produit principal du projet (`Opportunity.Product`) devient celui de la première ligne, pour les écrans et règles qui le lisent.

**Remise au-delà du plafond :** le devis passe à « À valider ». Il ne peut pas être généré ni envoyé avant la validation d'un responsable. La validation passe par un circuit de workflow (étape d'approbation) ou, à défaut, par le responsable de l'agent.

**Livré le 2026-11-10 :**
- `ProjectQuote` (tables `leads.project_quotes` et `project_quote_lines`) : une offre par projet, agrégat à part, pour ne toucher à aucune autre lecture du projet. Les totaux sont calculés par `QuoteTotals` et enregistrés.
- Routes :
  - `GET leads/opportunities/{id}/quote` (`lead:opportunity:read`) ;
  - `PUT …/quote` (`lead:opportunity:manage`), qui remplace les lignes et la remise globale ;
  - `POST …/quote/decision`, la décision du responsable.
  - Toutes respectent le périmètre de visibilité du projet.
- Calculs :
  - arrondi à l'unité pour XOF, XAF et GNF, à deux décimales sinon (`InstitutionCurrencies.MinorDigits`) ;
  - TVA de l'offre calculée par taux, sur la part du total HT de chaque taux après la remise globale ;
  - une remise supérieure au montant est refusée.
- Une ligne enregistrée et renvoyée avec son id, sur le même produit, garde son taux de TVA et le prix catalogue du moment de sa saisie, même si le produit a quitté le catalogue depuis. Une nouvelle ligne prend le taux d'aujourd'hui.
- **Choix fait sans retour du propriétaire, à confirmer :** la remise comparée au plafond se mesure par rapport au catalogue. Un prix baissé sous le prix catalogue compte comme une remise, au même titre qu'une remise saisie : remise effective = 1 − total HT ÷ Σ (quantité × prix catalogue, sinon prix saisi).
- Projet :
  - son montant estimé devient le total HT, et son produit celui de la première ligne, avec le gabarit qui suit, comme pour un changement de produit à la main ;
  - tant qu'il y a des lignes, un montant ou un produit tapé à la main est refusé (`PROJECT_AMOUNT_FROM_QUOTE_LINES`) ;
  - l'historique reçoit « Lignes du devis », « Remise acceptée » et « Remise refusée », en plus des changements de montant et de produit.
- Refus :
  - un projet de crédit, d'épargne ou d'assurance (`QUOTE_FINANCE_PROJECT`) : gabarit dont le montant n'est pas « Générique », ou produit d'un module ;
  - un produit qui n'est pas Standard, un produit inactif, une ligne sans prix quand le catalogue n'en a pas.
- Validation de la remise :
  - au-delà du plafond, l'offre passe « À valider » et le responsable hiérarchique du propriétaire (à défaut, le chef d'agence) reçoit une notification ;
  - décident ceux qui ont `lead:team:supervise` et le propriétaire dans leur équipe, ou qui couvrent toutes les agences ;
  - le propriétaire et l'auteur des lignes ne peuvent pas décider : double regard ;
  - un refus demande un motif, et l'agent est notifié de la décision ;
  - toute modification des lignes redemande la validation si la remise dépasse encore le plafond.
- **Pas fait :** la validation par un circuit de workflow. Seul le chemin « responsable de l'agent » existe.
- Défaut trouvé en recette et corrigé : les lectures de l'Administration et la notification, faites dans la transaction de la commande, faisaient échouer le second enregistrement (500, « This connection was used with an ambient transaction »). Elles passent maintenant hors transaction (`Outside.RunAsync`).
- Recette API sur tenant-socle, avec Christelle Aka comme agent et Mariam Bamba comme cheffe d'agence :
  - totaux exacts ;
  - montant tapé à la main refusé ;
  - remise à 19,66 % pour un plafond réglé à 10 %, donc « À valider » ;
  - décision refusée à l'agent (403), acceptée par la cheffe d'agence ;
  - notifications reçues des deux côtés ;
  - historique conforme.
- Données de recette :
  - deux projets « RECETTE Devis lot 3 » sur un lead de Christelle Aka, clos perdus avec le motif « Projet de recette » ;
  - le plafond de remise remis à vide.

### Lot 4. Lignes de projet (écran)

- **Saisie :** sur le tiroir de création du projet et sur la page du projet, un éditeur de lignes. C'est une collection modifiable sur place, l'exception admise à la règle `tas-table`. On choisit le produit dans le catalogue, et le prix, l'unité et la TVA se remplissent seuls ; la quantité et la remise se saisissent.
- **Calcul en direct :** les totaux se mettent à jour pendant la saisie, et une remise au-delà du plafond est signalée.
- **Projet finance :** pas d'éditeur de lignes, l'écran reste celui d'aujourd'hui.

**Livré le 2026-11-10 :**
- `components/quote-lines/` (lead-management) :
  - `quote-lines.models.ts` : le brouillon, et le calcul en direct qui reproduit `QuoteTotals`. C'est le serveur qui fait foi ;
  - `quote-context.ts` : les produits Standard actifs et les taux de l'institution ;
  - `quote-lines-editor.ts` : un signal form sur la collection, avec `applyEach`. Choisir un produit remplit la désignation, l'unité, le prix et la TVA, seulement au changement de produit. Les totaux et l'alerte de plafond s'affichent pendant la saisie ;
  - `quote-lines-section.ts` : la partie « Produits et prix » des tiroirs de création.
- Page du projet : carte « Produits et prix » (`project-quote.ts`), absente d'un projet que le serveur dit non chiffrable.
  - Lecture : tableau des lignes, totaux et bandeau d'état (« À valider », acceptée, refusée).
  - « Accepter la remise » et « Refuser », avec motif obligatoire, pour qui peut décider.
  - « Chiffrer le projet » ou « Modifier les lignes » ouvre l'éditeur ; l'enregistrement relit le projet.
- Tiroir « Modifier » du projet : avec des lignes, produit et montant sont affichés, pas modifiables. Le refus « montant obligatoire » mène à la carte.
- Tiroirs de création, depuis la fiche lead et depuis le tableau des projets :
  - « Chiffrer avec des produits » ouvre l'éditeur sur une première ligne ;
  - avec des lignes, les champs Produit et Montant disparaissent : le projet est créé avec le produit de la première ligne, puis ses lignes sont enregistrées ;
  - un échec des lignes laisse le projet créé, avec un message ;
  - la partie est absente quand le parcours du produit choisi est de crédit, d'épargne ou d'assurance.
- Le tableau de lecture des lignes est rattaché à l'exception `tas-table` de l'éditeur, comme résumé non paginé de celui-ci. Le plan ne nommait que l'éditeur : extension à confirmer par le propriétaire.
- Contrôle visuel sur tenant-socle, sans rien enregistrer :
  - lecture d'une offre validée ;
  - éditeur dans le tiroir du tableau : remplissage par le produit, remise globale de 20 %, totaux exacts, alerte de plafond ;
  - éditeur sur la page d'un projet ouvert.
- Trois défauts corrigés pendant ce contrôle :
  - l'éditeur s'ouvrait sans ligne, dans le tiroir et sur la page ;
  - le prix était trop serré dans le tiroir : les champs passent sur deux rangées.
- Constat : le projet « Crédit individuel — Seydou Traoré » de Recette Socle Seul propose « Chiffrer le projet ». Il vient de l'ancienne démo bancaire : son produit n'existe plus au catalogue de ce tenant socle et son parcours est le standard. « Recharger la démo » l'effacera ; sur un tenant avec le module, le produit est reconnu et les lignes sont refusées (testé au lot 3).

### Lot 5. Devis PDF

- **Génération :** « Générer le devis » produit le PDF avec QuestPDF. Il contient :
  - l'en-tête de l'institution : logo, raison sociale, adresse, identifiants ;
  - le destinataire, lead ou client, avec l'entreprise s'il y en a une ;
  - le numéro, la date et la validité ;
  - les lignes et les totaux ;
  - les conditions de paiement et le bas de page ;
  - la mention « Devis — ce document n'est pas une facture ».
- **Versions :** chaque génération reçoit un nouveau numéro de version (DEV-2026-000001 v2). Un devis envoyé n'est jamais modifié.
- **Rangement :** le PDF devient une **pièce du projet**, ce qui ouvre l'envoi par e-mail et par WhatsApp, la trace « Document envoyé » et l'onglet Documents du client, tous déjà en place.

**Livré le 2026-11-10 :**
- Route `POST leads/opportunities/{id}/quote/issue` (`lead:opportunity:manage`), qui accepte une validité et des conditions de paiement. Sans elles, ce sont celles de l'institution ; des conditions vides n'impriment rien.
- Numérotation :
  - le premier PDF prend le numéro suivant de l'institution (`IQuoteIssuer.NextNumberAsync`) ;
  - les suivants gardent ce numéro et ajoutent une version : `DEV-2026-000001 v2`.
- Refus : offre vide (`QUOTE_EMPTY`), remise en attente ou refusée (`QUOTE_DISCOUNT_NOT_APPROVED`), projet perdu. Un projet gagné peut encore recevoir un devis.
- Rangement :
  - le PDF est une pièce du projet sous la clé réservée `quote`, nommée « Devis DEV-2026-000001 v2.pdf » ;
  - chaque génération est enregistrée dans `leads.project_quote_issues` : numéro, version, validité, totaux, pièce ;
  - l'historique reçoit « Devis généré ».
  - Un PDF émis n'est jamais modifié : toute nouvelle génération crée une version.
- `ProjectQuote` porte `Number`, `IssuedVersion` et `LastIssuedAt`. `ChangedSinceIssue` signale des lignes modifiées après le dernier PDF.
- PDF (`ProjectQuotePdf`, QuestPDF, licence Community comme la documentation fournisseur) :
  - en-tête : logo s'il y en a un (un logo illisible est simplement omis), raison sociale, forme, adresse, coordonnées ; numéro, date et validité ;
  - destinataire : le client du projet s'il en a un, sinon le lead, avec l'entreprise d'abord. L'adresse postale n'est pas imprimée : chiffrée sur le lead, elle n'est jamais déchiffrée pour un devis. Seule la ville l'est ;
  - lignes, totaux et TVA par taux, conditions de paiement, validité ;
  - bas de page : RCCM, n° de compte contribuable, régime, texte libre de l'institution, « Devis — ce document n'est pas une facture. », pagination ;
  - « MODE TEST — sans valeur » sur un devis du bac à sable (tenant jumeau) seulement, pas sur une institution de démonstration ;
  - ligatures de police désactivées, pour que le texte du PDF reste copiable.
- Écran, carte « Produits et prix » :
  - « Générer le devis », ou « Nouvelle version du devis », avec la validité et les conditions préremplies par les réglages de l'institution ;
  - liste « Devis émis » : date, auteur, TTC, validité, envoi, avec téléchargement et boutons E-mail et WhatsApp qui ouvrent les tiroirs d'envoi existants, la pièce déjà cochée.
  - La table des pièces du projet et l'onglet Documents du client affichent « Devis » pour cette clé.
- Recette sur tenant-socle :
  - projet « RECETTE Devis lot 5 », versions 1 à 4 générées puis projet clos perdu ;
  - PDF lus : sans identité légale, avec identité complète (remplie puis vidée), avec un logo de test (déposé puis supprimé ; le champ logo de l'institution passe de « default.png » à vide, ce que les écrans traitent pareil) ;
  - **le numéro DEV-2026-000001 est consommé** : le prochain devis de Recette Socle Seul sera le 000002.
- Défauts corrigés pendant la recette : la mention « MODE TEST » sur une institution de démonstration, les ligatures, et le suffixe « jours » qui passait à la ligne.
- **Pas testé :** l'envoi effectif par e-mail (pas de boîte connectée) et par WhatsApp. Ce sont les chemins déjà recettés au plan d'envoi des documents, qui reçoivent ici une pièce ordinaire.

### Lot 6. Démo et recette

- **Démo socle :** prix et TVA sur les produits Standard, et des lignes sur le projet de BTP Lagune Construction.
- **Recette :**
  - lignes et totaux ;
  - remise au-delà du plafond, puis validation ;
  - génération et version du PDF ;
  - envoi par e-mail et par WhatsApp ;
  - montant estimé repris dans les rapports ;
  - projet finance inchangé.

**Lot 6 livré le 2026-11-10 :**
- Démo socle, Administration (`AdministrationDemoData`, chargement « Démo » d'une institution sans module) :
  - prix inventés sur les quatre produits Standard : Offre Essentiel 450 000 le forfait, Offre Premium 1 200 000 le forfait, Installation 150 000 l'intervention, Contrat de maintenance 360 000 l'an ;
  - plafond de remise à 10 % ;
  - en-tête « (démonstration) » sans aucun identifiant légal inventé, avec un bas de page qui le dit.
  - Chaque élément n'est posé que là où rien n'est réglé : ce qu'un présentateur a saisi reste.
- Démo socle, Leads (`LeadsCompanyDemoData.Offers`) :
  - BTP Lagune Construction : 3 installations, 3 Offre Premium, 1 contrat de maintenance à 5 %, puis le devis PDF ;
  - Ivoire Distribution : un projet « Offre Premium », 20 Offre Premium à 15 % et 20 installations, soit 13,3 % aux prix de démo. L'offre attend la validation de la cheffe d'agence Plateau.
  - Une offre n'est chiffrée que sur un projet qui n'en a pas : charger la démo à nouveau sur une ancienne ajoute les offres sans rien écraser. Sans stockage des pièces, seul le PDF est sauté.
- Recette de bout en bout sur Recette Socle Seul (2026-11-10). Elle a appliqué à la main ce que fait la démo, puisque la démo n'a pas été rechargée :
  - prix de démo posés sur les trois produits qui n'en avaient pas ; Offre Premium garde le prix du propriétaire (45 777 le carton) ;
  - BTP Lagune : total 929 331 HT et 1 096 611 TTC, montant du projet mis à jour, devis DEV-2026-000003 puis sa v2 ;
  - Ivoire Distribution : une remise globale de 15 % porte la remise effective à 17,98 %, l'offre passe « À valider » et le devis est refusé. La cheffe d'agence décide, puis viennent les versions 2 et 3 de DEV-2026-000004. À 16 %, soit 18,95 %, l'offre a été acceptée depuis l'écran par l'administratrice ;
  - rapport des projets : le montant du pipeline (15 510 809) égale la somme des montants des projets ouverts, lignes comprises ;
  - envoi : le devis figure dans les pièces envoyables par WhatsApp, mais rien n'est parti. Le canal WhatsApp est inactif sur ce tenant, et aucune boîte mail n'y est connectée ;
  - projet de crédit : vérifié par les tests (lot 3), pas sur ce tenant sans module.
- Défauts corrigés pendant la recette :
  - colonne Quantité du PDF trop étroite (« 3 intervention » passait à la ligne) ;
  - historique du projet affichant les codes bruts des quatre nouvelles entrées : libellés français ajoutés.
- **À trancher par le propriétaire.** Le plafond s'applique à la remise de toute l'offre, pas ligne par ligne. Une ligne à 15 % dans une offre à 3,5 % au total ne demande donc pas de validation.
- Constat hors plan : la recherche des leads ne trouve pas un lead par le nom de son entreprise : « Ivoire » ne trouve pas « Ivoire Distribution SA ».

## 6. À faire confirmer par un expert-comptable ou auprès de la DGI

Ces points règlent les valeurs par défaut et les textes du PDF, pas l'architecture :

- les mentions obligatoires d'un devis ou d'une proforma en Côte d'Ivoire ;
- la liste des biens au taux réduit de 9 %, que les sources consultées le 2026-11-10 ne donnent pas de façon concordante ;
- le traitement des remises dans la base imposable de la TVA.

Sources consultées le 2026-11-10 : taux de 18 % et 9 % ; directive UEMOA n° 02/98/CM/UEMOA (taux normal entre 15 % et 20 %) ; facture normalisée électronique obligatoire depuis 2025, avec des dates d'entrée en vigueur qui diffèrent selon les sources. Ce sont des sources secondaires ; les textes officiels n'ont pas été lus.

## 7. Hors de ce plan

- La facture, l'avoir et l'encaissement : ils relèvent du logiciel de facturation raccordé à la facture normalisée électronique.
- La conversion d'un devis accepté en commande ou en livraison.
- Les lignes multiples sur un projet de crédit, d'épargne ou d'assurance.
- Plusieurs monnaies dans un même devis : l'institution n'a qu'une monnaie.
