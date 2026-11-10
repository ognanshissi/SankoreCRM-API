# Prospection des grands comptes

**Statut : plan rédigé le 2026-11-09. Plan livré : lots 1 à 5 livrés, recette API et écrans faite le 2026-11-10 (non commités).**

## 1. Le besoin

Un tenant vend des produits livrables (marchandises, équipements, fournitures) et veut utiliser le CRM pour trouver et signer de gros clients : grossistes, distributeurs, entreprises, administrations.

La fonctionnalité appartient au **socle**. Elle sert toute entreprise qui vend, avec ou sans module, et ne porte aucune notion de banque, de microfinance ou d'assurance (voir [isolation-socle.md](isolation-socle.md)).

## 2. Décisions du propriétaire (2026-11-09)

| Sujet | Décision |
|---|---|
| Rattachement | Socle de base. |
| Priorité | Décrire l'entreprise et en tenir compte dans le score. |
| Lead de formulaire | Un lead qui remplit un formulaire pour un produit donné est un lead **chaud**. |
| Conversion d'un lead entreprise | Il devient une **personne morale**, même s'il a un nom de contact. Le nom du contact n'est pas reporté sur le client tant que les interlocuteurs par rôle n'existent pas. |

## 3. Ce qui existe déjà

- **Lead « entreprise » :** type `Corporate`, avec nom de l'entreprise, e-mail, téléphone et site web. Ces champs forment la section « Entreprise » du formulaire dynamique des leads.
- **Score de 0 à 100 :** profil, source, interactions, comportement, cohérence, localisation, adresse, et une pénalité d'inactivité. Le niveau Chaud, Tiède ou Froid se lit sur des seuils réglables par institution.
- **Effets du niveau Chaud :** délai de premier contact propre au niveau dans la règle de dispatching, premiers contacts chauds en tête de Ma journée avec un badge « Lead chaud », colonne et filtre Température, rapports.
- **Sources :** chaque source a sa valeur en points (0 à 20) et un « produit par défaut » saisi à la création.
- **Client personne morale :** raison sociale, secteur d'activité, numéro fiscal.

## 4. Ce qui manque

| Constat | Effet |
|---|---|
| **Le score est pensé pour un particulier.** Le profil donne des points pour la date de naissance (3) et la pièce d'identité (4), et la cohérence pour une pièce plausible (3). Une entreprise n'a ni l'une ni l'autre : elle ne peut pas atteindre ces 10 points, et le nom de l'entreprise ne lui en rapporte que 3. | Un prospect entreprise part toujours plus froid qu'un particulier, quelle que soit sa taille. |
| **Rien ne décrit la taille ou le potentiel.** Il n'y a ni secteur, ni effectif, ni volume d'achat attendu sur le lead. | Un grossiste et une boutique de quartier ont le même score. |
| **Le formulaire web ne transmet pas l'entreprise.** Les champs qu'une source peut remplir (`FieldMappingEngine.ValidTargetFields`, `InboundLeadFactory.CapturedFields`) ne comprennent ni le nom de l'entreprise, ni le site, ni le type de prospect. | Un lead B2B venu du site arrive comme un particulier. |
| **Le « produit par défaut » d'une source n'est pas appliqué.** `SourceSettings.DefaultProductCode` est enregistré par l'écran de création mais n'est lu nulle part à l'arrivée du lead : le produit vaut « Unknown » si le formulaire ne l'envoie pas. | Une page « Demander un devis pour le produit X » ne rattache pas le lead au produit X. |
| **Un formulaire produit n'est pas traité comme une demande.** Un lead de formulaire web obtient 12 points de source plus son identité et son adresse, soit environ 30 points : il est Froid. | La demande la plus explicite attend comme les autres. |

## 5. Lots

| Lot | Contenu | Résultat |
|---|---|---|
| **1. Décrire l'entreprise** | Nouveaux champs du lead entreprise ; saisie, import, formulaire web, fusion, fiche et liste | Chaque prospect entreprise porte son secteur, sa taille et son potentiel |
| **2. Score entreprise** | Critères entreprise à la place des critères particulier ; dimension « Potentiel » réglée par l'institution ; recalcul des leads existants | Un gros prospect a un score qui le montre |
| **3. Formulaire produit = lead chaud** | Produit par défaut appliqué ; option par source ; niveau Chaud au moins, tant que la demande est fraîche | Une demande produit par formulaire est traitée en priorité |
| **4. Orienter les gros prospects** | Critère « potentiel » dans le dispatching ; badge et filtre ; rapports | Les gros prospects vont aux bons commerciaux et se suivent à part |
| **5. Démo et recette** | Leads entreprise dans la démo socle ; recette sur un tenant socle | La démo socle montre une prospection B2B |

Le lot 3 ne dépend pas des lots 1 et 2 : il peut passer en premier si c'est plus urgent.

### Lot 1. Décrire l'entreprise

Nouveaux champs natifs du lead, dans la section « Entreprise », visibles seulement pour un lead entreprise :

| Champ | Forme | Pourquoi |
|---|---|---|
| Secteur d'activité | Liste de l'institution, livrée avec une liste neutre par défaut (commerce de gros, BTP, industrie, administration, santé, hôtellerie et restauration, transport, éducation…) | Cibler les secteurs qui achètent |
| Effectif | Tranches : 1 à 9, 10 à 49, 50 à 249, 250 et plus | Mesurer la taille sans exiger un chiffre exact |
| Volume d'achat annuel estimé | Montant dans la monnaie de l'institution | Le meilleur indice d'un « gros » client pour un vendeur de produits |
| Fonction du contact | Texte libre (acheteur, directeur, gérant…) | Savoir si l'on parle au décideur |

Le volume d'achat est distinct du « Montant souhaité », qui reste masqué par défaut et se saisit sur le projet.

Ces champs sont branchés partout où passe un lead :

- création et onglet Identité ;
- formulaire dynamique (placer, renommer, masquer) ;
- import de fichier ;
- formulaire web et webhooks, ce qui ajoute aussi le nom de l'entreprise, le site et le type de prospect aux champs qu'une source peut remplir ;
- fusion ;
- colonnes et filtres de la liste des leads ;
- copie par le mode test.

À la conversion, le nom de l'entreprise et le secteur sont reportés sur le client personne morale.

### Lot 2. Score entreprise

**Profil et cohérence d'un lead entreprise.** Ils restent plafonnés à 25 et 10 points, mais avec des critères d'entreprise : nom de l'entreprise, secteur, effectif, e-mail ou site de l'entreprise, téléphone de l'entreprise valide. Le score d'un particulier ne change pas.

**Nouvelle dimension « Potentiel », de 0 à 20 points, pour les leads entreprise seulement :**

| Critère | Points | Réglage de l'institution |
|---|---|---|
| Volume d'achat annuel estimé | 0 à 12, par paliers | Deux montants : « bon client » et « grand compte » |
| Effectif | 0 à 4 | — |
| Secteur ciblé | 0 ou 4 | Liste des secteurs ciblés |

La dimension s'ajoute aux autres comme l'adresse l'a fait, sans rien leur retirer, et le total reste plafonné à 100. Tant que l'institution n'a rien réglé, elle vaut 0 : aucun score existant ne bouge sans décision de l'administrateur.

**Réglage :** carte « Grands comptes » à côté de « Température des leads » (paliers de volume, secteurs ciblés). Un enregistrement recalcule les leads entreprise ouverts.

**Explication :** le détail du score affiche la dimension « Potentiel » et ce qu'elle a compté.

**Effet sur les tenants qui ont un module :** les nouveaux critères s'appliquent aussi aux leads entreprise d'un tenant bancaire (crédit PME, par exemple). Le biais contre les entreprises y existe aussi. La dimension « Potentiel » n'y joue que si l'administrateur la règle.

### Lot 3. Formulaire produit = lead chaud

1. **Produit par défaut appliqué.** À l'arrivée, un lead sans produit envoyé prend le produit par défaut de sa source. C'est la correction du constat du § 4.
2. **Option par source :** « Une demande pour un produit précis est un lead chaud ».
   - Cochée par défaut pour les canaux qui sont des formulaires : formulaire web, Facebook, Instagram et LinkedIn Lead Ads.
   - Décochée pour les autres canaux, et réglable sur chaque source.
3. **Règle.**
   - Un lead arrivé par une telle source, avec un produit actif du catalogue, est **au moins Chaud**, quel que soit son score.
   - Le score reste ce qu'il est : seul le niveau est relevé, et la raison est enregistrée (« Demande produit par formulaire »).
4. **Fin du relèvement.** Le niveau redevient celui du score dans trois cas :
   - un contact s'est conclu par « Pas intéressé » ;
   - le lead est disqualifié ou perdu ;
   - 30 jours se sont écoulés sans activité, au premier palier du refroidissement.

   La tâche de refroidissement de nuit s'en charge.
5. **Seuils.** Un changement de seuils, qui recalcule aujourd'hui le niveau de tous les leads à partir du seul score, doit respecter ce relèvement.
6. **Ce qui en découle sans travail en plus.** Comme le lead est Chaud, il reçoit le délai de premier contact « chaud » de la règle de dispatching et passe en tête de Ma journée. Le badge indique la raison.

### Lot 4. Orienter les gros prospects

- **Dispatching :** critère « potentiel » dans une règle (palier « grand compte », secteur ciblé), pour envoyer ces leads à une équipe ou à des commerciaux choisis.
- **Liste des leads :** colonne et filtre « Potentiel », et vue « Grands comptes ».
- **Ma journée :** badge « Grand compte » sur les tâches de ces leads.
- **Rapports :** onglet Leads ventilé par potentiel et par secteur, taux de conversion par palier.

### Lot 5. Démo et recette

- **Démo socle :** quelques prospects entreprise de tailles différentes (dont un grand compte), avec une source « Formulaire devis » liée à un produit Standard.
- **Recette sur « Recette Socle Démo » :**
  - saisie et import d'un lead entreprise ;
  - formulaire web avec et sans produit ;
  - niveau Chaud puis refroidissement ;
  - réglage des paliers et recalcul ;
  - dispatching vers l'équipe grands comptes ;
  - conversion en client personne morale.
- **Contrôle :** aucun libellé finance n'apparaît sur ces écrans.

## 6. Points à trancher

| Sujet | Recommandation |
|---|---|
| Ce qui fait un « gros » client | Le volume d'achat annuel estimé pèse le plus (12 points sur 20). L'effectif et le secteur complètent quand le volume n'est pas connu. |
| Durée du relèvement « Chaud » | Jusqu'à un contact négatif, une disqualification ou 30 jours sans activité. Une demande restée sans réponse un mois n'est plus une urgence. |
| Canaux concernés par la règle du lot 3 | Formulaires web et Lead Ads, réglable par source. Un webhook peut venir d'un formulaire comme d'un fichier : il reste décoché, à l'administrateur de décider. |
| Secteur du lead et secteur du client | Le secteur du client personne morale suit aujourd'hui la liste du core banking. Recommandation : la liste neutre sert au socle, et le libellé est reporté dans le détail d'activité du client à la conversion. L'alignement des codes est laissé au module Banking. |

## 7. Hors de ce plan

Ces sujets sont à prévoir dans un plan suivant, quand les gros clients sont signés :

- **Interlocuteurs par rôle :** acheteur, décideur, comptable, magasinier, sur la fiche entreprise.
- **Devis en lignes :** produit × quantité × prix, remise, sur le projet.
- **Segment « Grand compte » du client :** calculé sur le montant des projets gagnés sur 12 mois.
- **Commandes, stocks, livraison et facturation :** ils restent dans le logiciel de gestion commerciale, comme l'argent reste au core banking. Le CRM pourra afficher l'état d'une livraison reçue par webhook.

## 8. Lot 1 livré (2026-11-09)

**Nouveaux champs du lead**, colonnes de `leads.leads` (migration `AddLeadCompanyProfile`) :

| Champ | Clé du formulaire | Valeurs |
|---|---|---|
| Secteur d'activité | `activitySector` | 18 secteurs neutres (`LeadActivitySectors`), code ou libellé accepté |
| Effectif | `companySize` | `Micro` (1 à 9), `Small` (10 à 49), `Medium` (50 à 249), `Large` (250 et plus), ou un nombre de personnes |
| Volume d'achat annuel estimé | `estimatedAnnualPurchases` | montant dans la monnaie de l'institution |
| Fonction du contact | `contactJobTitle` | texte, 150 caractères |

Les quatre champs sont dans la section « Entreprise » du formulaire des leads, visibles pour un lead entreprise et proposés à la création. La liste des secteurs est fixe pour l'instant, identique pour toutes les institutions : la rendre modifiable par l'institution reste à faire.

**Où ils passent :**

- **Création :** le formulaire de création les envoie. `POST leads` les enregistre.
- **Modification :** `PUT leads/{id}` les modifie. Le tiroir « Modifier les informations » permet aussi de changer le type de prospect, ce qui était impossible avant.
- **Fiche :** l'onglet Informations les affiche.
- **Import :** nouvelles colonnes `ProspectType`, `CompanyEmail`, `CompanyPhone`, `Website`, `ActivitySector`, `CompanySize`, `EstimatedAnnualPurchases`, `ContactJobTitle`. Une ligne qui porte un `CompanyName` est une entreprise, sauf si `ProspectType` dit le contraire. Une valeur illisible fait échouer la ligne.
- **Formulaire web, webhook et retraitement :** les mêmes champs sont des cibles de correspondance. Le type se lit aussi en français (« Entreprise »). Une valeur illisible est écartée et le prospect est gardé.
- **Sources interrogées périodiquement :** le nom de l'entreprise, le secteur, l'effectif et la fonction sont repris.
- **Fusion :** l'option `TakeCompanyProfile` prend les quatre champs en bloc.
- **Liste, export et compteurs des vues :** filtres `prospectType`, `activitySector`, `companySize`. Un secteur inconnu répond 400. La liste a deux colonnes facultatives, « Secteur » et « Effectif ». L'export ajoute les colonnes de l'entreprise sous les noms de l'import.
- **Étape « Formulaire » d'un workflow :** elle peut exiger ces champs.

**Deux défauts corrigés au passage :**

- **Entreprise perdue à la création :** `Lead.Capture` écrivait vides le nom, l'e-mail, le téléphone et le site de l'entreprise, et rien ne les réécrivait. L'entreprise saisie à la création ou lue dans un import était perdue.
- **Colonne « Type » vide :** la colonne de la liste lisait le type comme un nombre et affichait toujours « — ».

**Non fait dans ce lot :** le report vers le client à la conversion. Il est tranché plus bas (§ 11).

- Le « détail d'activité » du client est une donnée de conformité (ce que l'institution a établi), pas ce que le prospect déclare.
- Un lead entreprise qui a un prénom ou un nom de contact devient aujourd'hui un client particulier. Seul un lead sans nom de contact devient une personne morale.

**Vérification :**

- **Tests :** 21 tests (`LeadCompanyProfileTests`). Les suites Leads (1 619), Administration (720) et Customers (1 158) passent.
- **API :** recette sur « Recette Socle Démo » (formulaire, création, lecture, modification, filtres, secteur refusé). Le lead de recette est archivé.
- **Front :** contrôle de types et compilation stricte des templates touchés. Pas de contrôle visuel.

## 9. Lot 2 livré (2026-11-09)

**Une entreprise est notée comme une entreprise.** Le score d'un particulier ne change pas.

- **Profil (25 points au plus) :** nom du contact 4, e-mail 3, nom de l'entreprise 4, secteur 3, effectif 2, e-mail ou site de l'entreprise 2, fonction du contact 2, questionnaire 5. La date de naissance et la pièce d'identité ne comptent plus pour une entreprise.
- **Cohérence :** le téléphone de l'entreprise (7 à 15 chiffres) remplace la pièce d'identité, pour 3 points.

**Dimension « Potentiel », 20 points au plus, ajoutée aux autres.** Le total reste plafonné à 100.

| Critère | Points |
|---|---|
| Volume d'achat annuel ≥ seuil « grand compte » | 12 |
| Volume d'achat annuel ≥ seuil « bon client » | 6 |
| Effectif : 250 et plus / 50 à 249 / 10 à 49 / moins de 10 | 4 / 3 / 1 / 0 |
| Secteur ciblé | 4 |

Elle vaut 0 pour un particulier, et pour tous les leads tant que l'institution n'a rien réglé.

**Réglage :**

- **Où :** carte « Grands comptes » sur l'écran Dispatching, à côté de « Température des leads », réservée à la permission `lead:dispatching-rule:manage`.
- **API :** `GET` et `PUT lead-settings/key-accounts`. Les deux seuils et les secteurs se règlent ensemble.
- **Refus :** un seuil « grand compte » qui ne dépasse pas le seuil « bon client » répond 400 `KEY_ACCOUNT_THRESHOLDS_INVALID`, un secteur inconnu 400 `LEAD_ACTIVITY_SECTOR_INVALID`.
- **Recalcul :** l'enregistrement lance `RescoreCompanyLeadsJob`, qui recalcule les leads entreprise ouverts et déjà notés et écrit une ligne d'historique `KEY_ACCOUNT_SETTINGS_CHANGED`. Leur température suit. Le statut ne bouge pas, et aucun événement « score changé » n'est publié.

**Détail du score sur la fiche :** une ligne « Potentiel de l'entreprise » quand elle compte, et « Inactivité » a enfin un libellé.

**Leads entreprise existants :** les nouveaux critères de profil s'appliquent à leur prochain recalcul : celui de nuit, une modification, ou l'enregistrement du réglage.

**Vérification :**

- **Tests :** 13 tests (`CompanyScoreTests`). La suite Leads passe (1 632).
- **API :** recette sur « Recette Socle Démo ». Un lead entreprise grand compte d'un secteur ciblé a eu 38 points à la capture, puis 58 après le réglage (potentiel 20), puis 38 après la remise à zéro. Le réglage est remis à vide et le lead archivé.
- **Front :** compilation stricte des templates. Pas de contrôle visuel.

## 10. Lot 3 livré (2026-11-09)

**Règle :** un lead arrivé par une source dont l'option est active, et qui demande un produit **actif** du catalogue, est au moins « Chaud », quel que soit son score.

- **Score :** il ne change pas. Seule la température est relevée.
- **Raison :** `Lead.ProductRequestAt` enregistre la date de la demande. Le rapprochement avec le catalogue est exact, sur le code ou le nom (`ProductLineLookup`).

**Fin du relèvement :** le niveau redevient celui du score dans trois cas :

- un contact s'est conclu par « Pas intéressé » ;
- le lead est clos ;
- 30 jours se sont écoulés sans activité. Le recalcul de nuit s'en charge.

**Où c'est branché :**

- **Lecture de la température :** `LeadScoreCalculator.IntentLevelOfAsync`, la seule définition, plus `ProductRequestHeat`. La capture, la qualification, les recalculs et la tâche de nuit passent par elle.
- **Changement de seuils :** il garde « Chaud » aux demandes encore fraîches.
- **Chemins qui marquent le lead :** formulaire web, webhook, retraitement d'une réception, source interrogée périodiquement, lead saisi avec une source.
- **Retraitement d'une réception :** il ne notait pas le lead créé. Il le note maintenant, comme toute capture.

**Option par source :** `LeadSourceConfig.ProductRequestIsHot`. Sans valeur, le réglage du canal s'applique : actif pour le formulaire web et les Lead Ads Facebook, Instagram et LinkedIn, inactif ailleurs, webhook compris. L'option est copiée par le mode test.

**Produit par défaut d'une source :** il est maintenant appliqué quand le formulaire, le webhook ou la source interrogée n'envoie aucun produit. Il était enregistré mais lu nulle part.

**Écrans :**

- **Source :** une carte « Demande de produit » sur l'onglet Général. On y choisit le produit par défaut, qui n'était modifiable qu'à la création, et on coche l'option.
- **Fiche :** sur la carte du score, « Chaud : a demandé ce produit par formulaire… ».
- **Ma journée :** le badge devient « Lead chaud · demande produit ».
- **Délai et ordre :** le lead étant « Chaud », il reçoit le délai de premier contact « chaud » de la règle de dispatching et passe en tête de « Ma journée », sans travail en plus.

**Vérification :**

- **Tests :** 11 tests (`ProductRequestHeatTests`), et la suite Leads passe (1 643).
- **API :** recette sur « Recette Socle Démo » avec la source SITE-WEB. À score égal (20), le lead qui demande CONTRAT-MAINTENANCE est « Chaud » et celui sans produit « Froid ». Leads archivés.
- **Couvert par les tests seulement :** la fin du relèvement (contact « Pas intéressé », 30 jours) et la tenue après un changement de seuils.
- **Front :** compilation stricte des templates. Pas de contrôle visuel.

## 11. Conversion d'un lead entreprise (2026-11-09, décision du propriétaire)

Un lead entreprise qui a un nom d'entreprise devient un client **personne morale**, même s'il a un nom de contact. Avant, un prénom ou un nom suffisait à en faire un client particulier nommé d'après son contact.

**Ce qui n'est pas envoyé au client :** les données personnelles du contact (date de naissance, genre, pièce d'identité, profession, situation matrimoniale). Elles ne sont pas celles de la société, et la pièce d'identité aurait fait passer la nouvelle société pour un doublon de la fiche client du contact.

**Ce qui l'est :** le téléphone et l'e-mail du lead restent les points de contact de la société.

**Ce qui ne change pas :**

- Un particulier qui indique un employeur reste un client particulier.
- Un lead entreprise sans nom d'entreprise reste un client particulier, faute de raison sociale.

**Le nom du contact est perdu à la conversion.** Il reviendra avec les interlocuteurs par rôle (§ 7).

**Le secteur déclaré n'est pas reporté** dans le « détail d'activité » du client, qui est une donnée de conformité.

**S'applique à tous les tenants :** un tenant bancaire verra ses leads entreprise devenir des personnes morales, avec le KYC qui va avec.

**Vérification :**

- **Tests :** 3 tests (`ConvertLeadHandlerTests`), et la suite Leads passe (1 646).
- **Recette :** sur « Recette Socle Démo », le lead « Issa RECETTE Traoré » de l'entreprise « RECETTE Conversion Grossiste SARL » a donné le client personne morale AG000051-2026-000024, sans prénom ni nom. Il est archivé, comme le client AG000051-2026-000023 créé par erreur au premier essai (l'API tournait encore sur l'ancien code).

## 12. Lot 4 livré (2026-11-09)

**Palier d'un lead entreprise :** « grand compte » quand son volume d'achat annuel atteint le seuil « grand compte », « bon client » au seuil « bon client » (`LeadPotentialTier`). Il est lu sur le réglage à chaque lecture et n'est jamais enregistré : changer les seuils reclasse tous les leads d'un coup. Un particulier n'a jamais de palier.

**Dispatching :**

- **Règle réservée :** une règle peut être « Réservée aux grands comptes » (`KeyAccountsOnly`). Elle reçoit les grands comptes avant toute autre règle, y compris celle qu'une source impose, et ne reçoit jamais un autre lead. Sans seuil réglé, elle ne reçoit personne.
- **Liste fermée d'agents :** une règle peut aussi désigner ses agents (`ReservedAgentIds`). Seuls les agents cochés reçoivent ses leads, et un agent ajouté plus tard n'y entre pas d'office. Dans l'éditeur, c'est le mode « Seulement les cochés » de la grille des agents ; l'autre mode, « Tous sauf les décochés », reste l'exclusion d'avant.
- **Simulation :** la simulation d'une règle tient compte des agents désignés.

**Liste des leads :**

- **Vue « Grands comptes » :** elle apparaît parmi les vues principales dès qu'il en existe.
- **Filtre « Potentiel » :** grands comptes ou bons clients, persisté dans l'URL (`potentiel=`).
- **Colonne « Potentiel » :** facultative.
- **Export :** il prend le même filtre.
- **Compteurs :** `GET leads/view-counts` ajoute `keyAccounts`.
- **Fiche et liste :** elles portent `potentialTier`.

**Ma journée :** badge « Grand compte » sur les tâches d'un grand compte. Un premier contact chaud devient « Lead chaud · grand compte ».

**Rapports, onglet Leads :** section « Entreprises » avec les entreprises captées sur la période par potentiel (grands comptes, bons clients, autres : captées, avec un projet, converties, taux) et par secteur. Le classeur Excel ajoute les feuilles « Potentiel » et « Secteurs ». La ventilation par potentiel n'apparaît que si un seuil est réglé.

**Vérification :**

- **Tests :** 6 tests (`KeyAccountDispatchTests`), et la suite Leads passe (1 652).
- **Recette API sur « Recette Socle Démo » :**
  - Montage : seuils 10 M / 50 M, règle « RECETTE Grands comptes » réservée à un agent.
  - Un lead entreprise à 80 M est classé grand compte et attribué à cet agent.
  - Le filtre, le compteur et le rapport le comptent.
  - Nettoyage : leads archivés, règle désactivée (aucune route ne la supprime), réglage remis à vide.
- **Front :** compilation stricte des templates. Pas de contrôle visuel.

## 13. Lot 5 : démo socle (2026-11-09)

**Nouvelle étape de démo :** `LeadsCompanyDemoData` (Order 65, chargements « Démo » des institutions **sans module** seulement). Chaque élément n'est créé que s'il manque :

- **Réglage grands comptes :** bon client dès 10 M, grand compte dès 50 M ; secteurs ciblés : commerce de gros, BTP, hôtellerie. Il n'est posé que si l'institution n'a rien réglé : un rechargement n'écrase pas ce qu'un présentateur a ajusté.
- **Source « Formulaire de devis »** (FORMULAIRE-DEVIS) : formulaire web, en brouillon, produit par défaut OFFRE-PREMIUM. Un lead qui demande un produit par ce formulaire est chaud.
- **Règle de dispatching « Grands comptes » :** réservée aux grands comptes, liste fermée de deux agents (agent-commercial-plateau, agent-commercial-yopougon).
- **Cinq prospects entreprise** (références DEMO-E001 à DEMO-E005, téléphones +225 07 98 00 1xx) :

| Entreprise | Secteur | Effectif | Volume | Palier | Source |
|---|---|---|---|---|---|
| Ivoire Distribution SA | Commerce de gros | 250 et plus | 85 M | Grand compte, chaud | Formulaire de devis |
| Clinique Sainte-Anne | Santé | 50 à 249 | 60 M | Grand compte | Parrainage |
| BTP Lagune Construction | BTP | 50 à 249 | 30 M | Bon client, avec un projet | Agence |
| Hôtel Les Palmiers | Hôtellerie | 10 à 49 | 12 M | Bon client, chaud | Formulaire de devis |
| Boutique Chez Mariam | Commerce de détail | 1 à 9 | 2 M | — | Agence |

Chaque prospect a un responsable : sa tâche « Premier contact » porte les badges dans « Ma journée ».

**Vérification faite :** 4 tests (`LeadsCompanyDemoDataTests`), et la suite Leads passe (1 656). Les validations réelles de la source et de la règle acceptent ces valeurs (relues dans les validateurs).

**Reste à faire :**

- **Charger la démo :** lancer « Charger la démo » (et non « Recharger », qui efface) sur « Recette Socle Démo » depuis l'application plateforme. Le chargement ajoute ce qui manque sans rien effacer.
- **Recette de bout en bout :**
  - réglage, source et règle présents ;
  - Ivoire Distribution SA chaud et grand compte ;
  - vue « Grands comptes » à 2 ;
  - badges dans Ma journée ;
  - section « Entreprises » des rapports.
- **Bloquant :** le 2026-11-09 au soir, Rider ne démarrait plus les services de l'AppHost (« Timeout … waiting for the IDE to start a run session »). `sankore-api` et `sankore-admin` sont arrêtés et ne peuvent être relancés que depuis Rider.

**Recette du lot 5 par l'API (2026-11-10)** sur « Recette Socle Seul » (https://tenant-socle.sankoreio.local:4222), démo chargée par le propriétaire :

- **Configuration :** réglage 10 M / 50 M, secteurs ciblés présents ; source FORMULAIRE-DEVIS (produit par défaut OFFRE-PREMIUM, « chaud » selon le canal) ; règle « Grands comptes » active, réservée, 2 agents.
- **Prospects :**

| Prospect | Palier | Température | Demande produit |
|---|---|---|---|
| Ivoire Distribution SA | Grand compte | Chaud (score 65) | oui |
| Clinique Sainte-Anne | Grand compte | Tiède | non |
| BTP Lagune | Bon client, projet de 10 M | Tiède | non |
| Hôtel Les Palmiers | Bon client | Chaud (score 56) | oui |
| Boutique Chez Mariam | Aucun | Froid | non |

- **Liste et rapports :** vue « Grands comptes » à 2 ; filtre conforme ; rapport par potentiel et par secteur rempli.
- **Reste :** le contrôle visuel. Chrome refuse le certificat auto-signé du front en https, et le propriétaire doit l'accepter une fois.

**Recette des écrans (2026-11-10)** sur la même institution, avec les comptes administrateur et agent commercial (Christelle Aka) de la démo.

**Conformes :**

- carte « Grands comptes » de l'écran Dispatching ;
- règle « Grands comptes » : case cochée, mode « Seulement les cochés », 2 agents sur 15 ;
- liste : vue « Grands comptes 2 », colonnes Type / Secteur / Effectif / Potentiel ;
- fiche d'Ivoire Distribution SA :
  - le profil de l'entreprise est complet ;
  - la carte du score affiche « Chaud : a demandé ce produit par formulaire… » ;
  - le détail du score montre « Potentiel de l'entreprise : 20 » ;
- carte « Demande de produit » de la source ;
- section « Entreprises » des rapports.

**Corrigés pendant la recette :**

- **Badges de Ma journée :** la « Ma journée » des agents (`my-day-agent`) n'avait aucun badge de raison. Elle affiche maintenant « Lead chaud » (ou « Lead chaud · demande produit ») et « Grand compte », dans le bandeau et sur les lignes. Le sous-titre commence par l'entreprise pour un lead entreprise.
- **Texte de priorité de l'éditeur de règle :** il annonçait qu'une règle grands comptes en position 1 attribuait tous les leads. Il décrit maintenant son vrai rôle. Une règle grands comptes ne compte plus comme règle « qui passe avant » pour les autres.
- **Aperçu de l'éditeur de règle :** il simulait n'importe quel lead. Pour une règle grands comptes, il prend maintenant des grands comptes, et se recharge quand on coche ou décoche la case.
- **Carte « Grands comptes » :** « (6 points) » et « (4 points) » étaient collés au texte, car Angular retire l'espace entre deux balises.

**Observations corrigées le 2026-11-10 :**

- **Liste des leads :** un lead entreprise s'affiche par son entreprise. Le contact et l'origine passent dessous, et l'avatar reprend les initiales de l'entreprise.
- **Badge de palier :** « Grand compte » ou « Bon client » apparaît dans l'en-tête de la fiche et dans l'aperçu, à côté de la température (`potentialBadge`).
- **Fiche d'un lead entreprise :** elle n'affiche plus date de naissance, genre ni pièce d'identité.
- **Anciennes données de démo bancaire** sur « Recette Socle Seul » (leads « Crédit individuel », tâches « Tontine ») : seul « Recharger la démo » les efface. La démo est désormais propre au socle, entreprises comprises. Le propriétaire doit la lancer depuis l'application plateforme, en retapant le nom de l'institution.
