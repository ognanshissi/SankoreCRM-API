# Tests de charge k6

Deux scénarios sur le même parcours, « la journée d'un agent » (`lib/agent-day.js`) : ouverture de
l'application et compteurs du menu, Ma journée (agenda, tâches, validations), liste et recherche de
leads, fiche lead, liste et fiche client — et, seulement avec `-e WRITES=1`, la saisie d'un lead.

| Scénario | Pour quoi | Charge |
|---|---|---|
| `scenarios/smoke.js` | non-régression après un lot | 1 agent, 3 parcours (~2 min) |
| `scenarios/load.js` | combien d'agents simultanés l'application tient | montée jusqu'à `MAX_VUS`, palier de 5 min (~14 min) |

## Avant de lancer

1. **Installer k6** : `brew install k6`.
2. **Un tenant de test, jamais la production.** Chaque connexion est historisée et chaque commande
   auditée ; avec `WRITES=1` des leads sont créés (nom `K6 Test …`).
3. **Rien ne doit partir vers l'extérieur sur ce tenant** : pas de fournisseur e-mail/SMS configuré,
   pas de connexion WhatsApp, core banking ni téléphonie. La saisie d'un lead peut déclencher un
   dispatch, des tâches et des notifications.
4. **Des données en volume réaliste** (quelques dizaines de milliers de leads et de clients) : sur
   une base vide, les listes et recherches paraissent rapides à tort.
5. **Des comptes de test** : copier `data/users.example.csv` en `data/users.csv` (ignoré par git)
   et y mettre un compte par agent simulé, avec des rôles d'agent. Les créer par l'import
   d'utilisateurs.

### Les limites de débit de l'API

- Toute route `api/v1` est limitée à **200 requêtes par minute et par compte**. Le parcours est
  rythmé (pauses de 2 à 6 s) pour rester sous 60/min par compte : avec un compte par utilisateur
  virtuel, la limite n'est jamais atteinte. Le scénario de charge s'arrête au premier 429, parce
  qu'à partir de là il mesure la limite et non l'application.
- La connexion est limitée à **60 par minute et par adresse IP** : `setup()` connecte chaque
  compte une seule fois et espace les appels au-delà de 50 comptes.

## Lancer

Depuis ce dossier :

```bash
# Non-régression (API locale lancée par Aspire sur 5080)
k6 run -e TENANT_FQDN=http://localhost:4222 scenarios/smoke.js

# Avec la saisie de leads
k6 run -e TENANT_FQDN=http://localhost:4222 -e WRITES=1 scenarios/smoke.js

# Montée en charge jusqu'à 50 agents, résumé enregistré
mkdir -p results
k6 run -e BASE_URL=https://api.staging.example -e TENANT_FQDN=https://crm.staging.example \
       -e MAX_VUS=50 --summary-export results/load-$(date +%F).json scenarios/load.js
```

Variables : `BASE_URL` (défaut `http://localhost:5080`), `TENANT_FQDN` (défaut
`http://localhost:4222`), `USERS_FILE` (chemin ABSOLU, sinon k6 le lit relativement à
`lib/auth.js`), `WRITES=1`, `MAX_VUS` (défaut 50).

## Lire le résultat

- Les mesures sont regroupées par écran (`name`) : `leads (liste)`, `leads/{id}`, `clients (liste)`…
- `forbidden_calls` compte les 403 : le rôle du compte n'a pas ce droit (KYC, par exemple). Ce n'est
  pas une erreur, mais un compte d'agent qui n'en a que des 403 mesure peu de chose.
- Seuils : moins de 1 % d'erreurs ; 95 % des réponses sous 1 s (smoke) ou 1,5 s (charge).
- Pendant le test, regarder le tableau de bord Aspire (ou Seq) pour les temps par route et les
  erreurs, et `pg_stat_statements` sur PostgreSQL pour les requêtes lentes.
