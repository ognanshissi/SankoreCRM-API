# Points bloquants : données et envois du core banking

**Constat du 2026-11-05, établi en lisant le code des connecteurs. Aucun n'a été testé contre un vrai core banking.**

Le CRM ne remplace pas le core banking : il affiche ce que celui-ci lui transmet et lui envoie les dossiers. Les fonctions ci-dessous sont donc bloquées tant que les éditeurs n'ont pas fourni ce qui manque.

## 1. Ce qui est bloqué pour les utilisateurs

| Fonction attendue | Pour qui | État |
|---|---|---|
| Encours et capital restant dû sur la fiche client | Gestionnaire de comptes, de crédit | Bloqué : aucune donnée de prêt réelle |
| Jours de retard et date de fin d'un crédit | Gestionnaire de comptes, de crédit | Bloqué : l'affichage est prêt, la donnée n'arrive pas |
| Portefeuille « Mes clients » avec encours et impayés | Gestionnaire de comptes | Bloqué : le filtre « Mes clients » existe, sans les chiffres |
| Alerte « nouvel impayé » | Gestionnaire de comptes | Bloqué |
| Alerte « crédit arrivant à échéance » | Gestionnaire de comptes | Bloqué (l'alerte existe pour l'assurance) |
| Envoi d'une demande de prêt | Gestionnaire de crédit | Développé côté CRM, bloqué par connecteur : voir section 2 |

## 2. La situation par core banking

| Core banking | Lire les prêts | Envoyer une demande de prêt | Ce qui manque |
|---|---|---|---|
| **Temenos** | Non : pas de code de lecture des prêts | Non : pas de code d'envoi | La documentation de l'API prêts (Transact), puis son développement |
| **SAB** | Non : refusé | Non : refusé | La spécification de l'éditeur, qui bloque toutes ses fonctions |
| **Amplitude** | Non : refusé | Mode service : refusé. Mode fichier : voir ci-dessous | Le catalogue de services « Up » |
| **Perfect Vision** | Non : aucune lecture, par fichiers seulement | Mode service : refusé. Mode fichier : voir ci-dessous | La spécification du fichier d'échange, et une décision sur la lecture (section 4) |
| **Simulateur de test** | Oui, données fictives | Oui | Rien : il sert aux tests seulement |

**Mode fichier :** une connexion en mode fichier dépose les commandes dans un fichier sans passer par le connecteur. Une demande de prêt peut donc techniquement partir, mais le format n'a été validé avec aucun éditeur : rien ne garantit qu'elle sera comprise à l'arrivée.

**Conséquence à connaître :** avec Temenos, les comptes sont lus et les prêts restent vides. Une liste de crédits vide veut dire « non communiqué », jamais « ce client n'a pas de crédit ». Aucun écran ne doit la présenter autrement.

## 3. Ce qu'il faut demander à chaque éditeur

**Pour lire les prêts, par client :**
- référence du prêt et code produit ;
- montant accordé et capital restant dû ;
- monnaie ;
- statut ;
- date de déblocage et date de fin ;
- nombre de jours de retard.

**Pour envoyer une demande de prêt :**
- champs obligatoires, codes produits et monnaies acceptés ;
- comment le core banking accuse réception : référence retournée, délai, motifs de refus ;
- si une demande venue d'un système externe est bien acceptée.

**Dans les deux cas :**
- le moyen d'accès : service interrogeable ou fichier ;
- la fréquence de mise à jour ;
- un environnement de test.

## 4. Décisions à prendre

| Décision | Pourquoi | Recommandation |
|---|---|---|
| **Quel core banking traiter en premier ?** | Chacun demande un développement à part | Celui de la première institution cliente |
| **Un fichier d'extraction peut-il alimenter des chiffres financiers ?** (Perfect Vision, Amplitude avant « Up ») | Le code l'interdit aujourd'hui : un fichier non signé ne doit pas pouvoir écrire la situation financière d'un client | Ne l'autoriser qu'avec une garantie sur la source, par exemple un fichier signé ; à trancher avec la conformité |

## 5. Ce qu'il restera à développer une fois les données disponibles

1. La lecture des prêts dans le connecteur retenu.
2. Des colonnes de synthèse sur l'instantané du client (`cbs_customer_snapshot`) : encours total, plus grand retard, prochaine échéance. Comptes et prêts y sont stockés en bloc, ce qui ne permet ni filtre ni liste transversale.
3. Le portefeuille « Mes clients » avec ces chiffres et le filtre « En impayé ».
4. Les alertes « nouvel impayé » et « crédit arrivant à échéance ».

## 6. Ce qui fonctionne déjà sans cela

- Le raccourci « Mes clients » dans la liste des clients.
- La date de fin d'un contrat d'assurance, quand l'assureur la transmet, et l'alerte 30 jours avant.
- L'affichage d'une date de fin et d'un retard sur un crédit, dès qu'un connecteur les fournira, sans autre développement à l'écran.
- Le modèle de projet « Dossier de crédit », de la demande à la décision, sans core banking.
- L'envoi d'un dossier approuvé et son suivi sur la page du projet. La page dit clairement si le core banking de l'institution ne reçoit pas encore les demandes de prêt, et dans ce cas il faut saisir le dossier de son côté.

**Limite de ce test (recette du 2026-11-05) :** une réponse « accepté » n'a été vue que dans les tests automatisés. Dans l'application lancée, le simulateur repart de zéro à chaque requête et ne connaît que son client de démonstration (`CUS-CI-0001`). Une demande de prêt pour un autre client y est donc toujours refusée : « No customer with reference… ». L'envoi et l'affichage du refus ont été vérifiés au navigateur, pas l'acceptation.
