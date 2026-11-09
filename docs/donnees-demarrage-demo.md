# Données de démarrage et de démo d'un tenant

**Statut : plan validé le 2026-11-08. Lots 0 à 5 livrés et vérifiés avec l'API lancée (tenant de référence : « Démo Complète »), non commités.**

## 1. Ce qui est décidé

Choix du propriétaire du 2026-11-08 :

| Sujet | Décision |
|---|---|
| Deux jeux | « Démarrage » : paramétrage seul, pour une vraie institution, sans personne fictive. « Démo » : le démarrage, plus des comptes et des dossiers fictifs. |
| Dossiers de démo | Volume réaliste : leads à tous les statuts, clients, projets à chaque étape, KYC, tâches du jour, réclamations. |
| Connexion des comptes de démo | Un mot de passe commun lu dans la configuration du serveur, jamais écrit dans le code. Les comptes sont actifs d'emblée, sans e-mail d'activation. |
| Adresses des comptes de démo | `contact+<profil>.<institution>@talisoft.net` (2026-11-08) : les e-mails de la démo partent vers une vraie boîte, et plusieurs démos coexistent. |
| Assureur en démo | Le simulateur, disponible sur toutes les plateformes mais seulement pour une institution de test (2026-11-08). |
| Lancement | Depuis l'app plateforme : une option à la création du tenant, et les boutons « Charger le démarrage », « Charger la démo » et « Recharger la démo » sur un tenant existant. |
| Produits du démarrage | Créés **actifs** ; l'institution désactive ce qui ne la concerne pas. |
| Agences du démarrage | « Siège » et « Agence principale », à renommer. |
| Core banking en démo | Aucune connexion, pas même le simulateur. |

Hypothèses retenues, à corriger si besoin : Côte d'Ivoire, francs CFA (XOF), interface en français.

## 2. Comment ça marche

Le chargement suit exactement le chemin de l'effacement des données de test, qui fonctionne déjà :

1. **La plateforme enregistre la demande.** Elle crée une demande « Démarrage » ou « Démo » dans le registre (`Sankore.Admin`).
2. **L'API la traite.** Une tâche Hangfire de l'API (`TenantDataLoadJob`) va chercher les demandes en attente chaque minute avec la clé de service, puis exécute le chargement module par module.
3. **La plateforme affiche le résultat.** L'API renvoie au registre le nombre d'objets créés, ou l'erreur ; l'écran du tenant se met à jour toutes les 5 secondes.

Chaque objet est créé par la commande ordinaire de son module, sous le compte système du tenant. Il passe donc par la même validation et le même audit qu'une saisie à l'écran, comme pour « Copier en production ». Le chargement est rejouable : il repère ce qui existe déjà par une clé naturelle (code produit, code de source, e-mail, numéro de pièce, référence « DEMO-… ») et ne crée que ce qui manque.

**Garde-fous :**
- **Démo réservée aux tenants de test :** la démo n'est acceptée que sur un tenant marqué test, et jamais sur le jumeau « Mode test » d'une vraie institution. Dans un jumeau, l'équipe est celle de la production : 50 leads distribués préviendraient de vrais agents.
- **Adresses de démo maîtrisées :** les comptes de démo ont des adresses `contact+<profil>.<institution>@talisoft.net`, une boîte de l'éditeur. Comme ce sont des comptes de l'équipe, le mode test les laisse recevoir les e-mails de la démo ; les clients et leads fictifs, eux, ne reçoivent rien.
- **Mot de passe obligatoire :** sans mot de passe de démo configuré (`DemoData:Password`), la démo est refusée avec un message clair.
- **Simulateur d'assureur réservé aux tests :** il est disponible partout, mais une connexion « Fake » ne peut être créée, activée ni utilisée que par une institution de test (ou en développement). Une institution qui cesse d'être de test perd l'accès à sa prochaine opération.
- **Pas de core banking :** aucune connexion core banking n'est créée en démo. Un KYC validé ne déclenche donc aucun envoi vers un core banking.
- **Réinitialisation :** « Vider les données » (section « Données de test ») efface les dossiers et garde les comptes et le paramétrage. « Recharger la démo » efface de la même façon puis recharge la démo, dans la même tâche (voir § 6).

## 3. Contenu

### Démarrage (vraie institution)

| Objet | Contenu | Remarque |
|---|---|---|
| Fiche institution | Nom du tenant, XOF, français | Créée avec le compte système s'il manque |
| Agences | « Siège » et « Agence principale » | Noms neutres, à renommer |
| Produits bancaires | Compte courant, compte épargne, crédit individuel, crédit de groupe, crédit agricole, tontine | Seulement si le module Banking - Microfinance est actif |
| Produits d'assurance | Santé, prévoyance décès, assurance emprunteur | Seulement si le module Assurance est actif |
| Sources de lead | Passage en agence, parrainage, appel entrant, import de fichier, formulaire web (brouillon) | |
| Gabarits de projet | Gabarit par défaut, « Dossier de crédit » pour la famille Prêt (avec son circuit analyste puis chef d'agence) | |
| Questionnaires | Un questionnaire de qualification simple par famille de produits | Publiés |
| Déjà en place | Règle de dispatching, types de tâches, circuit KYC, modèles d'e-mail et de SMS | Seeders existants, relancés pour un tenant neuf |

Les produits sont créés actifs (choix du propriétaire) : l'institution désactive ceux qu'elle ne propose pas et relit les conditions des autres.

### Démo, en plus du démarrage

**Comptes.** Un compte par profil, rattaché à la bonne agence et à son responsable :

| Profil | Nombre | Agence |
|---|---|---|
| Administrateur | 1 | Siège |
| Responsable commercial (SalesManager) | 1 | Siège |
| Chef d'agence | 2 | Plateau, Yopougon |
| Agent | 4 | 2 par agence |
| Agent commercial | 2 | Plateau, Yopougon |
| Gestionnaire de comptes | 1 | Plateau |
| Gestionnaire de crédit | 1 | Plateau |
| Analyste crédit | 1 | Siège |
| Conformité (RegulationManager) | 1 | Siège |
| Caissier | 1 | Plateau |

**Paramétrage de démo.** Les agences Plateau et Yopougon sous le siège, deux territoires et des objectifs mensuels pour les agents. L'assurance passe par un assureur simulé : connexion « Fake », produits assureur activés et fiche d'information de démonstration.

**Dossiers, datés sur les 90 derniers jours :**

| Dossiers | Volume |
|---|---|
| Leads | Environ 50, à tous les statuts, sur toutes les sources, chauds, tièdes et froids |
| Clients | Environ 20 (15 particuliers, 5 entreprises) |
| KYC | En collecte, en validation, validés (signés par deux comptes différents) et un refusé |
| Projets | À chaque étape, dont un dossier de crédit en attente de l'avis de l'analyste |
| Tâches et rendez-vous | Du jour, de la semaine, et quelques-uns en retard |
| Réclamations | 2 ou 3, dont une en retard |

Les numéros de téléphone et les adresses des clients et des leads sont fictifs. En tenant de test, aucun envoi ne part vers eux.

### Comptes de démo (lot 2)

Les adresses suivent le modèle `contact+<profil>.<institution>@talisoft.net` (choix du propriétaire, 2026-11-08), réglable par `DemoData:EmailTemplate`. `<institution>` est le nom de l'institution simplifié (« Démo Caisse Assurance » → `demo-caisse-assurance`) : une adresse de connexion ne sert qu'une fois sur toute la plateforme, et plusieurs démos doivent pouvoir coexister. Elles arrivent dans une vraie boîte : les e-mails de la démo (nomination d'un chef d'agence, alertes, « Mot de passe oublié ») partent et peuvent être montrés. La création d'un compte de démo refuse toute adresse que ce modèle ne produit pas.

Le mot de passe, commun à tous les comptes, est celui de `DemoData:Password` dans la configuration du serveur. En production, il passe par la variable d'environnement `DemoData__Password`.

| Adresse (`contact+….<institution>@talisoft.net`) | Nom | Profil | Agence | Responsable |
|---|---|---|---|---|
| administrateur | Aminata Touré | Administrateur | Siège | — |
| responsable-commercial | Serge Kouadio | Responsable commercial | Siège | Aminata Touré |
| analyste-credit | Salimata Sangaré | Analyste crédit | Siège | Aminata Touré |
| conformite | Didier Assi | Conformité | Siège | Aminata Touré |
| chef-agence-plateau | Mariam Bamba | Chef d'agence | Agence Plateau | Serge Kouadio |
| chef-agence-yopougon | Ibrahim Coulibaly | Chef d'agence | Agence Yopougon | Serge Kouadio |
| agent-plateau-1, agent-plateau-2 | Fatou Diallo, Jean-Marc Yao | Agent | Agence Plateau | Mariam Bamba |
| agent-commercial-plateau | Christelle Aka | Agent commercial | Agence Plateau | Mariam Bamba |
| gestionnaire-comptes | Nadia Koné | Gestionnaire de comptes | Agence Plateau | Mariam Bamba |
| gestionnaire-credit | Patrick Ehui | Gestionnaire de crédit | Agence Plateau | Mariam Bamba |
| caissier | Rokia Traoré | Caissier | Agence Plateau | Mariam Bamba |
| agent-yopougon-1, agent-yopougon-2 | Awa Ouattara, Moussa Konaté | Agent | Agence Yopougon | Ibrahim Coulibaly |
| agent-commercial-yopougon | Yannick Brou | Agent commercial | Agence Yopougon | Ibrahim Coulibaly |

### Dossiers de démo (lot 4)

| Dossiers | Ce qui est chargé |
|---|---|
| Clients | 15 particuliers et 5 entreprises dans les agences Plateau et Yopougon, chacun avec son conseiller ; pièces « CI-DEMO-… », adresses `@example.com`, numéros `+225 07 99 0…` |
| Réclamations | 3 : une reçue, une accusée réception, une traitée |
| Leads | 48 sur toutes les sources : 18 nouveaux, 12 contactés, 12 qualifiés, 3 perdus, 3 disqualifiés ; 6 laissés à la distribution automatique ; numéros `+225 07 98 0…` ; captés sur les 12 dernières semaines |
| Projets | 6 sur des leads qualifiés ; 4 dossiers de crédit sur des clients : un en « Demande », un en « Constitution du dossier », deux dans la file de l'analyste crédit |
| Tâches et rendez-vous | 12 tâches réparties sur les 6 agents (du jour, en retard, à venir) et 4 rendez-vous en agence dans la semaine |
| KYC | 3 dossiers validés, 2 en attente du chef d'agence, 1 refusé, les autres en collecte |

Chaque action est faite au nom de la personne qui l'aurait faite (agent, chef d'agence, analyste), et chaque commande dans un périmètre de dépendances qui lui est propre, comme une requête HTTP. Un KYC est validé sans service de biométrie : pièces de démonstration (un PDF qui le dit, une silhouette), vérification qui reste en attente, validation manuelle, puis signatures par d'autres comptes (règle des quatre yeux).

Limites connues :
- **Dates du jour :** les clients, les KYC et les projets sont datés du jour du chargement. Seuls les leads et les tâches ont un passé.
- **Assurance :** pas de souscription de démonstration ; l'assureur simulé et ses produits sont prêts, la souscription se fait pendant la démo.
- **Distribution :** les leads d'assurance laissés à la distribution automatique ne trouvent aucun agent titulaire d'une carte professionnelle, et la mise en file d'attente échoue (bug antérieur, signalé à part).

## 4. Ce qu'il faut construire

| Besoin | Existant | À construire |
|---|---|---|
| Demande, suivi, résultat | Effacement des données de test (registre, tâche API, section de l'écran du tenant) | Une demande « chargement » sur le même modèle |
| Compte système d'un tenant neuf | `RegisterCommand` (créé aujourd'hui à la main par `create-root`) | L'appeler si le compte manque |
| Tâches récurrentes d'un tenant neuf | Enregistrées seulement au démarrage de l'API | Les enregistrer après le chargement |
| Comptes de démo actifs sans e-mail | Rien : la création envoie toujours le lien d'activation, et la réinitialisation par l'administrateur expire le mot de passe | Une commande réservée aux tenants de test |
| Fiche d'information assureur | Téléversement écrit directement dans l'endpoint | Une commande, pour pouvoir l'appeler |
| Dates dans le passé | Seule la capture d'un lead accepte une horloge | Un « vieillissement » des dossiers de démo propre à chaque module, après leur création |
| Commandes internes (projets, tâches, KYC…) | Les commandes ne sont pas visibles hors de leur module | Une partie du chargement dans chaque module, comme les seeders actuels |

## 5. Découpage en lots

| Lot | Contenu | Résultat | Taille |
|---|---|---|---|
| **0** | Demande de chargement (registre, tâche API, section « Données de départ » de l'écran du tenant, option à la création), compte système, tâches récurrentes | Le circuit tourne de bout en bout, sans rien charger | M |
| **1** | Démarrage : fiche institution, agences, produits, sources, gabarits, questionnaires | Une vraie institution démarre avec un paramétrage relisible | M |
| **2** | Comptes de démo : domaine bloqué, commande dédiée, comptes par profil, responsables, territoires, agences de démo | On se connecte à chaque profil | M |
| **3** | Assurance de démo : assureur simulé, produits assureur, fiche d'information | Le parcours assurance se montre | S |
| **4** | Dossiers de démo et vieillissement | Chaque écran a quelque chose à montrer, rapports compris | L |
| **5** | « Recharger la démo », documentation, recette complète | Une démo se remet à neuf en un clic | S |

## 6. Recharger la démo (lot 5)

Après une présentation, la démo se remet à neuf en un clic, depuis l'écran du tenant dans l'app plateforme (section « Données de départ »).

**Utilisation**
1. Ouvrir le tenant de démo dans l'app plateforme.
2. Dans « Recharger la démo », ressaisir le nom exact de l'institution, puis cliquer sur « Recharger la démo ».
3. La demande apparaît « En attente » dans l'historique, puis « Terminé » au bout de deux à trois minutes, avec le nombre de lignes effacées et ce qui a été recréé.

**Ce qui se passe**
- **Effacement :** les dossiers sont effacés exactement comme par « Vider les données » : leads, clients, projets, tâches, rendez-vous, KYC et leurs pièces, réclamations, conversations, circuits en cours.
- **Ce qui reste :** les comptes de démo et leur mot de passe, les agences, le catalogue, les sources, les gabarits, les questionnaires, l'assureur simulé et ses produits.
- **Rechargement :** les dossiers de démo sont recréés, datés du jour du rechargement. Les identifiants changent : un lien vers un ancien dossier ne mène plus nulle part.

**Garde-fous**
- **Institution de test seulement :** comme la démo, jamais sur une vraie institution ni sur un jumeau « Mode test ». L'API le vérifie à nouveau, sur le registre lu à l'instant, avant d'effacer quoi que ce soit.
- **Nom à ressaisir :** le rechargement efface ; le registre refuse un nom qui ne correspond pas (`CONFIRMATION_MISMATCH`).
- **Jamais à la création :** une institution neuve n'a rien à effacer ; la création refuse « DemoReload » et propose la démo.
- **Un seul traitement à la fois :** un rechargement est refusé pendant un vidage (`DATA_WIPE_IN_FLIGHT`), et un vidage pendant un chargement (`DATA_LOAD_IN_FLIGHT`).

**Côté technique :** le registre enregistre un chargement de type `DemoReload`. `TenantDataLoadJob` le lit comme « Démo » précédée d'un effacement : il lance chaque `ITenantDataWipe` (les modules de « Vider les données »), puis les étapes de la démo. Le résumé ajoute « Lignes effacées ».

**Recette du 2026-11-09 sur « Démo Complète »**
- **Rechargement :** 605 lignes effacées. Les mêmes volumes sont recréés : 20 clients, 3 réclamations, 48 leads, 10 projets, 12 tâches, 4 rendez-vous, 3 KYC validés, 2 en validation et 1 refusé. Aucun identifiant de lead, de client ni de KYC n'est commun avec l'avant.
- **Comptes :** les 15 comptes se connectent, chacun avec son rôle.
- **Files d'attente :** l'analyste crédit a 2 dossiers dans sa file. Chaque chef d'agence a 1 KYC à signer.
- **Ce que voit chaque profil :** les agents voient leurs leads (6 à 9 chacun), leurs clients et leurs tâches. La conformité voit tous les KYC.
- **Second rechargement, après le correctif :** 572 lignes effacées, même contenu recréé. Chaque agent voit autant de dossiers KYC que de clients ; l'analyste a toujours 2 dossiers, chaque chef d'agence 1 KYC à signer.
- **Correctif trouvé pendant la recette :** les KYC de démo avaient pour responsable le compte système, qui a créé les clients. Aucun agent ne voyait donc les dossiers de ses clients. L'étape KYC confie maintenant chaque dossier de démo au conseiller du client, comme le fait la création d'un client au guichet.

## 7. Institution sans module (2026-11-09)

Voir `docs/isolation-socle.md`, lot 5. Pour une institution « CRM seul » :
- **Démarrage :** quatre produits Standard et un questionnaire « Qualification commerciale » remplacent le catalogue bancaire et d'assurance.
- **Démo :** pas de compte crédit ni caisse, des leads et des projets sur les offres Standard, et aucune réclamation « Crédit ».

Un chargement ou un vidage interrompu par un redémarrage de l'API est repris au bout de 20 minutes.

