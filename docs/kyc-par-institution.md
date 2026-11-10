# KYC activable par institution et par produit

Demande du propriétaire (2026-11-10) : « le KYC activable par institution et par produit ».

## Ce qui existe déjà

- **Par produit** : `BeneficiaryPolicy.RequiredKycTier` (Aucun / Simplifié / Complet), réglé dans
  Produits › Règles du produit. « Aucun » est réservé à la catégorie Standard
  (`BENEFICIARY_POLICY_KYC_NONE_STANDARD_ONLY`). Un client converti sur un tel produit est créé
  `Active` sans dossier (`Client.ActivateWithoutKyc`, `ClientCreatedEvent.KycRequired = false`).
- **Par institution** : rien. Le KYC est dans le socle et il est toujours actif.

## Décisions du propriétaire (2026-11-10)

1. **Qui** : l'administrateur de l'institution, depuis Paramétrage › Conformité. Le changement est
   tracé dans l'audit. Aucun accord de la plateforme n'est demandé : le KYC est du socle, il ne
   coûte rien.
2. **Modules** : désactivable **en socle seul**. Si « Banking - Microfinance » ou « Assurance » est
   actif, le KYC est obligatoire, et activer l'un de ces modules rallume le KYC.
3. **Par produit** : inchangé. « Aucun KYC » reste réservé à Standard. Quand le KYC de
   l'institution est coupé, le réglage du produit est masqué.
4. **Existant à la désactivation** :
   - les dossiers KYC restent consultables, avec un bandeau « consultation seule » ;
   - les clients « KYC en attente » passent actifs, avec une trace dans leur historique ;
   - à la réactivation, ils sont repris par l'étape de projet qui exige le KYC.

## Règle unique

> Le KYC est **exigé** pour un client si le KYC de l'institution est **en vigueur** et que le
> produit ne dit pas « Aucun ».
>
> KYC **en vigueur** = interrupteur activé **ou** un module Banking - Microfinance / Assurance
> actif.

La seconde moitié est une ceinture de sécurité. Elle garantit le KYC dès qu'un module est en
vigueur, même avant que l'interrupteur enregistré ait été remis à « activé ».

Tout ce qui décide « faut-il un KYC ? » passe par un seul lecteur, `ITenantKyc` (Shared.Kernel,
sur le modèle d'`ITenantModules`). `GetAsync(tenantId)` rend un `TenantKycState` : `SwitchedOn`,
`RequiredByModule` et `InForce`.

- Le lecteur est implémenté par Administration (`TenantKycReader`), avec un cache d'une minute
  invalidé à la bascule.
- Il ferme par défaut : une réponse illisible vaut « en vigueur ».
- Un jumeau du mode test lit l'interrupteur de sa production.
- Il n'y a pas de lecteur « consultable » côté serveur. Désactivé, le KYC reste lisible (GET),
  seules les écritures sont refusées. Le front décide d'afficher l'entrée de menu selon qu'il
  existe des dossiers.

## Lots

### Lot 1 — Interrupteur (serveur) — livré le 2026-11-10, non commité

**Livré** : 766 tests Administration au vert. Recette API sur Recette Socle Seul : agent refusé (403), désactivation, deuxième désactivation sans effet, réactivation, audit et événements vérifiés en base. L'institution est laissée avec le KYC activé. Le refus avec un module n'est couvert que par les tests : aucune institution de recette n'a de module.


**Données et API**
- `ComplianceSettings.KycEnabled`, à `true` par défaut et pour une institution sans ligne.
- Migration `AddKycEnabled`.
- Le champ est ajouté à `ComplianceSettingsDto`, côté Administration et PublicApi.

**Endpoint**
- Nouveau : `PUT compliance-settings/kyc` `{ enabled }`, qui renvoie `{ enabled, changed }`.
- Sous le droit de gestion de la conformité, refusé avec `KYC_REQUIRED_BY_MODULE` tant qu'un module
  Banking - Microfinance ou Assurance est actif.
- Une demande de module en attente ne bloque pas.
- Chaque bascule est tracée dans l'audit : qui, quand, et dans quel sens.

**Lecteur et retour du KYC avec un module**
- `ITenantKyc` et son cache, invalidé à la bascule et au changement de modules.
- Quand un module est approuvé, le job qui relit déjà le registre remet `KycEnabled` à `true`.
  Sinon, désactiver plus tard le module couperait le KYC sans que personne l'ait demandé.

**Exposition et mode test**
- `CurrentUserDto.KycInForce`. L'écran de conformité lit `kycEnabled` et `kycRequiredByModule`
  dans `GET compliance-settings`.
- Le jumeau du mode test ne copie pas le réglage : il lit celui de sa production. La route de
  bascule est fermée en mode test (`LiveModeWritesOnly`).

**Tests** : bascule, refus avec un module, rallumage par le module, valeur par défaut.

### Lot 2 — Effets de la désactivation (serveur) — livré le 2026-11-10, non commité

**Livré**. Tests : Customers 1160, KYC 811, Leads 1678, Shared 513, Administration 766, Workflow 201,
tous au vert.

Recette sur Recette Socle Seul, avec l'accord du propriétaire :
- les 16 clients de démonstration en attente sont passés actifs en 2 s, motif dans l'historique ;
- les 2 circuits de validation en cours ont été retirés (instances annulées, dossiers déliés) ;
- KYC coupé, un client créé est actif d'emblée, sans dossier ;
- KYC coupé, une écriture KYC répond 409 `KYC_DISABLED` et une lecture répond 200 ;
- KYC réactivé à la fin.

**Écarts avec le texte ci-dessous** :
- Le filtre `RequireKycInForce()` répond 409. Il exempte seulement la révocation d'un lien.
- `CreateKycFileHandler` et l'émission d'un lien refusent `KYC_DISABLED` : c'est la porte unique, et
  les consumers n'ont pas été modifiés un par un.
- Les circuits de validation en cours sont retirés de la file (`TenantKycDisabledKycConsumer`).
- L'étape de circuit « Vérification KYC » passe d'elle-même, motif « vérification sans objet ».
- Pas de lecteur « consultable » côté serveur.


**Création de clients**
- La création directe (particulier, personne morale), l'import et la conversion de lead créent le
  client `Active` via `ActivateWithoutKyc`, avec `KycRequired = false` quand le KYC n'est pas en
  vigueur.
- Le bot WhatsApp passe par la conversion.

**Décisions « faut-il un KYC ? »**
- `ConvertLeadHandler.IsKycRequiredAsync`, `ProjectStepGate`, `LeadCompletenessCalculator` et
  l'approbation passent par `ITenantKyc` avant le palier du produit.
- À la réactivation, `ProjectStepGate` rouvre déjà le dossier (`KycRequestedIntegrationEvent`)
  quand le client n'en a pas, puis exige l'approbation. Il n'y a rien à ajouter pour reprendre
  les clients.

**Module KYC**
- Un filtre `.RequireKycInForce()`, calqué sur `TenantModuleEndpointFilter`, est posé sur les
  groupes `kyc-files`, `kyc-settings` et le KYC public à distance.
- GET et HEAD passent si `IsReadableAsync`. Les écritures sont refusées avec `KYC_DISABLED`.
- Les consumers `ClientCreatedKycConsumer` et `KycRequestedConsumer` ne font rien.
- Les jobs suivants sautent les institutions dont le KYC n'est pas en vigueur :
  - révisions : `KycReviewOrchestratorJob`, `ContractAnniversaryReviewJob` ;
  - invitations à distance et leur expiration ;
  - relances de validation à distance (SLA) ;
  - rejeu de vérification ;
  - `KycLimitWatchJob`.

**Au moment de la bascule vers « désactivé »**, dans la même commande ou par un événement
`TenantKycDisabled` consommé par Customers :
- Les clients `PendingKyc` passent `Active` via `ActivateWithoutKyc`, avec le motif « KYC désactivé
  pour l'institution » dans l'historique des statuts.
- Les tâches KYC ouvertes (validation, complément, révision) sont annulées, avec ce même motif.
  C'est un choix par défaut à confirmer à la recette : sinon les agents gardent des tâches qu'ils
  ne peuvent plus traiter.
- Les dossiers KYC ne sont pas modifiés.

**Non concernés**
- Les clients `KycRejected` gardent leur statut.
- Les plafonds KYC ne s'appliquent qu'aux connexions core banking, qui exigent Banking -
  Microfinance. Ils ne sont pas concernés en socle seul.

**Tests** :
- création directe et conversion, KYC coupé ;
- bascule avec des clients en attente ;
- écriture KYC refusée, lecture permise ;
- jobs sautés ;
- réactivation, puis le gate du projet rouvre le dossier.

### Lot 3 — Écrans (front) — livré le 2026-11-10, non commité

**Vérifications** :
- `tsc` et ngc strict passent sur les six bibliothèques touchées ;
- tests front : catalogue du paramétrage 5, `common` 63.

**Recette navigateur sur Recette Socle Seul**, KYC coupé :
- Conformité : la carte et l'étiquette « KYC désactivé » apparaissent.
- Menu : « Dossiers KYC » reste, puisqu'il reste des dossiers.
- `/kyc` : le bandeau « consultation seule » s'affiche, sans « Décider ».
- Liste des clients : plus de colonne, de raccourcis ni de panneau KYC ; le bouton devient
  « Consulter le dossier KYC ».
- Onglet KYC du client : le bandeau s'affiche et l'encart des plafonds est masqué.
- Rapports : plus d'onglet KYC.
- Paramétrage : « Règles KYC » est masqué, et `/settings/kyc` redirige avec un message.
- Produit : plus de « KYC à distance ».

Le KYC a ensuite été réactivé depuis l'écran, avec la confirmation.

**Ajout serveur trouvé en recette** : `ListKycFilesHandler` ne rend plus aucun dossier « en attente
de vous » quand le KYC est coupé, ce qui supprime les « Décider » et le badge.


Aujourd'hui, le KYC n'est masqué que par les droits. Aucun réglage de l'institution ne joue.

Le KYC n'est pas un module payant : on n'ajoute pas d'entrée à `TENANT_MODULES`. Le schéma suivi
est quand même celui des modules.

**Socle commun**
- `TenantKycService`, dans `crm/common`, lit `CurrentUserDto.kycInForce`. « Consultable » se
  déduit de l'existence d'au moins un dossier.
- Gardes `kycGuard()` et `kycReadGuard()`, calquées sur `hasModuleGuard` et
  `canReadModuleGuard`.
- `PortalNavItem` gagne un prédicat d'affichage, qui sert aussi à la palette de commandes.

**Paramétrage**
- Paramétrage › Conformité, carte « KYC » : interrupteur avec confirmation, qui annonce combien de
  clients en attente vont passer actifs.
- Quand un module est actif, l'interrupteur est verrouillé avec ce message : « Obligatoire avec le
  module X ».
- « Règles KYC » (`settings-catalog`) n'est affiché que si le KYC est en vigueur.
- Produit :
  - le select « Niveau KYC requis » (`beneficiary-policy-editor`) est masqué ;
  - l'onglet « KYC à distance » (`edit-product`) est masqué.
- Étapes des projets : le marqueur « Approbation — KYC validé » devient « Approbation ».
- Le type d'étape « Vérification KYC » du constructeur de circuits est marqué indisponible.
- La règle de segment « Statut KYC requis » est masquée.

**Écrans KYC (`/kyc`)**
- Visibles en lecture seule si l'institution a des dossiers (`readable`), absents sinon.
- Bandeau « consultation seule », actions masquées, enrôlement fermé.
- `/kyc/verification` (lien public) répond que le lien n'est plus valable.
- Le badge du menu n'est plus calculé.

**Clients**
- Liste : la colonne « Parcours KYC », les filtres « KYC à démarrer / en cours », le panneau
  latéral et le bouton « Démarrer le KYC » disparaissent.
- Fiche :
  - l'onglet KYC suit `readable` (en lecture seule), avec une garde sur la route enfant `kyc`, qui
    n'en a aucune aujourd'hui ;
  - le bandeau « KYC initié », l'alerte « KYC en attente » et le tag « Statut KYC » disparaissent.
- Création : plus de « KYC initié » ni de redirection `?kyc=initie`.

**Leads et projets**
- « Dossier KYC » est retiré du bandeau client existant et de la fiche lead.
- Le lien « Voir le KYC du client » du projet est retiré.
- L'exigence « Le dossier KYC du client doit être validé » n'apparaît plus.
- Les textes de l'assistant de conversion sont revus.

**Ma journée et tâches**
- Les compteurs « dossiers KYC à valider », « révisions KYC » et « pièces à renouveler »
  disparaissent, ainsi que les validations KYC de la liste d'approbations.
- Le sujet « Un dossier KYC » n'est plus proposé à la création d'une tâche.

**Rapports** : l'onglet KYC est absent, et « en attente de KYC » est retiré de l'onglet Clients.

**Compte** : le lien « journal des accès KYC » suit `readable`.

**Hors du lot** (déjà fermés par le module Assurance, donc invisibles en socle seul) : le niveau
KYC des produits assureur, les groupes solidaires, les plafonds core banking.

**Vérification** : `tsc` et le ngc strict de chaque lib touchée.

### Lot 4 — Recette — faite le 2026-11-10 : plan terminé, non commité

**Résultats sur Recette Socle Seul.** Les produits de l'institution sont tous réglés sur « Aucun KYC »
et son parcours n'a pas d'étape KYC. La recette s'est donc faite sur un produit dédié, RECETTE-KYC
(Standard, Simplifié), avec son propre parcours (Prospection, Dossier client, Approbation).

KYC coupé :
- le dossier client s'ouvre sur un client **actif**, KYC `NotStarted`, sans dossier ;
- le projet A atteint l'approbation et le lead est converti.

Mode test :
- le jumeau lit le KYC coupé (`kycInForce: false`) ;
- la bascule y est refusée (409).

KYC réactivé :
- le projet B atteint « Dossier client » et un dossier KYC s'ouvre (Collecting) ;
- l'approbation est refusée `PROJECT_KYC_NOT_APPROVED` ;
- le client reste actif.

**Données rangées** :
- les 4 projets sont clos perdus ;
- le lead de recette sans agence est archivé ;
- le client est archivé ;
- le produit RECETTE-KYC est retiré, et son parcours reste.

**Données restantes** : le dossier KYC en collecte de ce client archivé, et celui du lot 2.

**Non recetté en réel** : le verrouillage par un module, faute d'institution de recette qui en ait un.


Sur une institution en socle seul (Recette Socle Seul), dans cet ordre :

1. Désactiver le KYC, puis vérifier :
   - le client en attente est passé actif ;
   - les tâches KYC sont annulées ;
   - les écrans sont masqués ou en consultation seule ;
   - les nouveaux clients sont actifs.
2. Réactiver, puis vérifier :
   - un projet qui atteint l'étape « dossier client » rouvre le KYC ;
   - l'approbation l'exige.
3. Sur une institution avec un module : vérifier que l'interrupteur est verrouillé et que l'API
   refuse la désactivation.
