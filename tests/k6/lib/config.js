// Réglages communs, lus dans l'environnement : `k6 run -e BASE_URL=… -e TENANT_FQDN=… scenarios/…`.

export const BASE_URL = (__ENV.BASE_URL || 'http://localhost:5080').replace(/\/$/, '');

// Le tenant est résolu sur cet en-tête, comme pour le front (`x-tenant-fqdn`).
export const TENANT_FQDN = __ENV.TENANT_FQDN || 'http://localhost:4222';

// Les comptes de test, un par utilisateur virtuel. Ce fichier contient des mots de passe : il est
// ignoré par git (voir .gitignore), seul `data/users.example.csv` est versionné.
export const USERS_FILE = __ENV.USERS_FILE || '../data/users.csv';

// Les scénarios d'écriture créent des leads : jamais par défaut.
export const ALLOW_WRITES = __ENV.WRITES === '1';

export const API = `${BASE_URL}/api/v1`;
