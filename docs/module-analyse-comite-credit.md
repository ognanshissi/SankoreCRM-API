# Module activable « Analyse et comité de crédit »

**Statut : proposition à valider, rien n'est démarré (2026-11-05).**
À ne lancer qu'après l'étape 0 : si les institutions font déjà bien ce travail dans leur core banking, ce module n'a pas lieu d'être.

## 1. L'idée

Permettre à une institution d'instruire une demande de crédit dans le CRM, depuis l'analyse jusqu'à la décision du comité, puis de transmettre le dossier approuvé au core banking.

Le CRM ne remplace pas le core banking. Le module s'arrête à la décision.

| Dans le module | Hors du module, au core banking |
|---|---|
| Fiche d'analyse et ratios indicatifs | Déblocage des fonds |
| Avis de l'analyste et du gestionnaire | Échéancier officiel, intérêts, pénalités |
| Séances de comité, votes, procès-verbal | Remboursements et restructurations |
| Décision et conditions | Classement des impayés, provisions |
| Envoi du dossier approuvé et suivi de la réponse | États réglementaires et comptabilité |

Les chiffres du module (mensualité estimée, taux d'endettement) servent à éclairer une décision. Ils ne font jamais foi : le montant et l'échéancier officiels restent ceux du core banking.

## 2. Troisième module : décidé le 2026-11-05

Deux choix passés du propriétaire (2026-10-22) étaient en tension avec ce module :
- il n'y a que **deux modules** : « Banking - Microfinance » et « Assurance » ;
- le **crédit / prêts (M04) est hors périmètre**, et `loan:create` ainsi que le menu « Prêts » ne doivent pas y être rattachés.

| Option | Effet | Avis |
|---|---|---|
| **A. Troisième module, qui exige Banking - Microfinance** | Activation et facturation à part, les institutions qui n'en veulent pas ne voient rien | **Retenu (propriétaire, 2026-11-05)** |
| B. Inclus dans Banking - Microfinance | Plus simple, mais tous les clients du module reçoivent un écran que beaucoup n'utiliseront pas | Non |

Le découpage rejeté en 2026 portait sur l'intégration core banking. Celui-ci ajoute un métier, sans redécouper l'existant. La dépendance vient de ce que les produits de type crédit et la connexion au core banking appartiennent déjà à Banking - Microfinance.

Le module n'utilise ni `loan:create` ni le menu « Prêts » : ils désignent une gestion de prêts qui reste exclue.

## 3. Qui s'en sert

| Rôle | Ce qu'il fait |
|---|---|
| Gestionnaire de crédit | Monte le dossier, le soumet à l'analyse, présente au comité si besoin, informe le client |
| Analyste crédit | Remplit la fiche d'analyse, rend un avis motivé, demande des compléments |
| Membre du comité | Consulte les dossiers de la séance, vote, motive |
| Président du comité | Ouvre et clôt la séance, arbitre selon les règles, signe le procès-verbal |
| Administrateur | Règle les seuils, les niveaux de comité et le modèle de fiche |

## 4. Le parcours

1. **Demande** : un projet est ouvert sur un produit de type crédit. Le gabarit de projet collecte montant, durée, objet et pièces. Le KYC suit la règle du produit.
2. **Soumission à l'analyse** : le gestionnaire soumet le dossier complet ; il arrive dans la journée de l'analyste.
3. **Analyse** : revenus, charges, engagements, garanties. Les engagements déjà connus du core banking sont repris en lecture seule. Les ratios se calculent. L'analyste rend son avis : favorable, favorable sous conditions, ou défavorable.
4. **Orientation** : le montant et le niveau de risque désignent qui décide (délégation en agence, comité d'agence, comité central).
5. **Comité** : le dossier est inscrit à une séance, les membres votent, le procès-verbal est produit.
6. **Décision** : accordé, accordé sous conditions, ajourné ou refusé. Le projet avance ou s'arrête en conséquence.
7. **Transmission** : le dossier accordé part une seule fois au core banking. Sa réponse s'affiche sur le projet, et un refus technique est signalé au gestionnaire.

## 5. Fonctions

### Fiche d'analyse
- Modèle réglable par institution : sections et champs, y compris libres.
- Revenus et charges déclarés, avec la pièce qui les justifie.
- Engagements en cours : repris du core banking, complétés à la main pour ceux d'autres établissements.
- Garanties : type, valeur estimée, décote.
- Ratios indicatifs : mensualité estimée, taux d'endettement, reste à vivre, couverture par les garanties.
- Seuils d'alerte réglés par l'institution, sans valeur réglementaire inscrite dans le code.
- Une fiche par demande, avec un historique des versions.

### Comité
- Niveaux réglables : qui décide selon le montant, le produit et le risque.
- Membres par niveau, quorum, règle de majorité, voix du président.
- Séance : date, ordre du jour, dossiers, présents.
- Vote motivé, avec abstention et déclaration de conflit d'intérêts.
- Procès-verbal en PDF, figé à la clôture.
- Décision à distance (vote sans réunion) : à confirmer, voir section 10.

### Suivi
- « Mes dossiers à analyser » et « Mes séances » dans Ma journée.
- Alertes : dossier affecté, complément demandé, séance à venir, vote attendu, décision rendue, délai dépassé.
- Rapports : délais par étape, taux d'accord, motifs de refus, volume par analyste et par niveau.

## 6. Règles à ne pas contourner

- **Quatre yeux :** celui qui a monté le dossier ou l'a analysé ne vote pas dessus, et une délégation ne s'exerce pas sur son propre dossier. Le module fait respecter cette règle lui-même, comme le KYC, car le moteur de workflow ne la garantit pas.
- **Conflit d'intérêts :** un membre qui en déclare un est exclu du vote, et le quorum se recalcule.
- **Décision figée :** une fois la séance close, on ne modifie ni vote ni procès-verbal. Une révision passe par une nouvelle décision qui renvoie à la précédente.
- **KYC :** pas de décision favorable tant que le KYC exigé par le produit n'est pas au niveau requis.
- **Envoi unique :** une seule transmission par décision, avec une clé d'idempotence.
- **Motif de refus :** le motif interne n'est jamais transmis automatiquement au client, comme pour le KYC. Ce qu'on lui dit reste à définir (section 10).
- **Périmètre :** un analyste ou un membre ne voit que les dossiers de son périmètre d'agence ou de son niveau de comité.
- **Traçabilité :** chaque action a son auteur et sa date, et l'audit le retrouve.

## 7. Ce qu'on réutilise et ce qu'il faut construire

| Besoin | Existant réutilisable | À construire |
|---|---|---|
| Dossier | Projet, gabarit par produit, champs, pièces jointes, points à cocher | Une liaison entre le projet et sa demande de crédit |
| Circuit | Workflows liés à une étape, escalade, relances, délais | Un type d'étape « Décision de crédit », qui attend le module comme « Vérification KYC » attend le KYC |
| Engagements | Instantané core banking (`CbsCustomerSnapshot`, `CbsLoan`) | Leur reprise dans la fiche |
| Envoi | Commande `SubmitLoanApplication` et son exécution par les adaptateurs | Le déclencheur métier : **aujourd'hui rien ne crée cette commande** |
| Activation | `TenantModules`, `.RequireTenantModule`, écran Modules, garde front | L'enregistrement du module et sa dépendance |
| Alertes | Cloche, notifications, e-mails | Les nouvelles catégories |
| PDF | Génération déjà utilisée pour la fiche d'assurance | Le gabarit de procès-verbal |
| Séances et votes | Rien | Tout |
| Ratios | Rien | Tout |

La règle « Action Integration n'envoie rien » (2026-10-29) reste valable : elle évite un double envoi là où Integration envoie déjà. Pour une demande de prêt, aucun envoi n'existe aujourd'hui, et le module serait le seul expéditeur.

## 8. Conception technique

- **Nouveau module `Sankore.Modules.CreditAnalysis`**, schéma `credit`, avec son `*.PublicApi`. Il référence le projet et le client par identifiant opaque, sans clé étrangère entre modules.
- **Lectures et envois par les contrats publics** : `ILeadsModule`, `ICustomersModule`, `IKycModule`, `IIntegrationModule`.
- **Chiffrement :** revenus et charges sont des données sensibles. Ils passent par une protection de champs à clé propre (`AddCreditFieldProtection`, enregistrement à clé comme M02 et M13, car `AddFieldProtection` ne s'appelle qu'une fois). Pas de lecture de ces montants dans les listes.
- **Événements** par l'outbox : `CreditDecisionRendered`, entre autres, sans valeur personnelle.

| Entité | Contenu |
|---|---|
| `CreditApplication` | Projet, client, produit, montant, durée, statut |
| `CreditAnalysis` | Version, champs, ratios calculés, avis, analyste |
| `CreditGuarantee` | Type, valeur, décote, pièce |
| `CreditExternalCommitment` | Engagement saisi à la main |
| `CommitteeLevel` | Seuils, membres, quorum, règle de majorité |
| `CommitteeSession` | Date, ordre du jour, présents, statut, procès-verbal |
| `CommitteeVote` | Membre, dossier, sens, motif |
| `CreditDecision` | Résultat, conditions, séance, décision précédente, référence d'envoi |

**Droits proposés :** `credit:application:read`, `credit:analysis:write`, `credit:committee:vote`, `credit:committee:chair`, `credit:settings:manage`.

**À prévoir comme pour les autres modules :**
- Désactivation : consultation seule, dossiers en cours lisibles, écritures refusées, conformément à la décision du 2026-10-23.
- Mode test : réglages copiables comme les autres objets de configuration ; un envoi depuis le mode test reste à cadrer.
- Purge : durées de conservation à définir avec le module « Purge de données » ; la décision et le procès-verbal sont des pièces de preuve.

## 9. Découpage en lots

Prérequis : Ma journée pour tous les rôles, les alertes, et l'affichage des données du core banking (feuille de route, étapes 1 à 3).

| Lot | Contenu | Résultat | Taille |
|---|---|---|---|
| **0** | Entretiens avec 2 ou 3 institutions, réponses à la section 10 | Décision de lancer ou non | S |
| **1** | Module, activation, droits, demande liée au projet, fiche d'analyse et ratios | L'analyste instruit et rend son avis | L |
| **2** | Type d'étape « Décision de crédit », files de travail, alertes | Le dossier circule entre gestionnaire et analyste | M |
| **3** | Niveaux, séances, votes, quorum, règles de quatre yeux, procès-verbal | Le comité décide dans le CRM | L |
| **4** | Effet de la décision sur le projet, envoi au core banking, suivi de la réponse | Le dossier accordé arrive au core banking | M |
| **5** | Rapports, copie en mode test, conservation | Le module se pilote et se vend | M |

Après le lot 2, le module est déjà utile à une institution qui garde son comité hors du CRM et saisit simplement la décision. Ce point d'arrêt permet de vérifier l'intérêt avant d'investir dans le comité.

## 10. Questions ouvertes

**Pour le propriétaire**
1. Le module est-il facturé à part ? *Recommandation : oui, comme les autres.*
2. Une institution peut-elle l'utiliser sans core banking connecté, en notant la décision à la main ? *Recommandation : oui, l'envoi devient alors facultatif.*

**Pour les institutions (lot 0)**
3. Où se font l'analyse et le comité aujourd'hui, et qu'est-ce qui gêne ?
4. Quels niveaux de décision et quels seuils ?
5. Le comité se réunit-il, ou vote-t-il aussi à distance ?
6. Quels ratios et quelles garanties sont réellement regardés ?
7. Que dit-on au client en cas de refus, et qui le lui dit ?
8. Quelles exigences de conservation s'appliquent au procès-verbal ?
9. Le core banking accepte-t-il bien une demande venue d'un système externe ? `SubmitLoanApplication` existe dans le code, mais n'a jamais servi dans un parcours réel.

## 11. Critère pour lancer

Lancer le lot 1 seulement si au moins deux institutions confirment que l'analyse ou le comité se fait aujourd'hui hors de leur core banking (tableur, papier, messagerie) et qu'elles sont prêtes à payer pour le faire dans le CRM.

Sinon, le modèle de projet « Dossier de crédit » de la feuille de route suffit.
