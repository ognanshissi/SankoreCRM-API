import http from 'k6/http';
import { sleep } from 'k6';
import { SharedArray } from 'k6/data';
import exec from 'k6/execution';
import { API, TENANT_FQDN, USERS_FILE } from './config.js';

/** Les comptes du CSV `email,password`, lus une fois et partagés entre utilisateurs virtuels. */
export const users = new SharedArray('users', () => {
  const lines = open(USERS_FILE).split(/\r?\n/).map((l) => l.trim()).filter((l) => l && !l.startsWith('#'));
  const rows = lines.length && lines[0].toLowerCase().startsWith('email') ? lines.slice(1) : lines;
  if (rows.length === 0) throw new Error(`Aucun compte dans ${USERS_FILE} : copiez data/users.example.csv en data/users.csv.`);
  return rows.map((line) => {
    const [email, password] = line.split(',').map((v) => v.trim());
    return { email, password };
  });
});

/**
 * Connecte chaque compte UNE fois, dans `setup()`. La connexion est limitée à 60 par minute et par
 * adresse IP (règle `auth`) : au-delà de 50 comptes on espace les appels, sinon les derniers
 * reçoivent un 429 et le test démarre avec des utilisateurs virtuels sans jeton.
 */
export function loginAll(max = users.length) {
  const count = Math.min(max, users.length);
  const pause = count > 50 ? 1.1 : 0;
  const sessions = [];

  for (let i = 0; i < count; i++) {
    const { email, password } = users[i];
    const res = http.post(`${API}/auth/login`, JSON.stringify({ email, password }), {
      headers: { 'Content-Type': 'application/json', 'x-tenant-fqdn': TENANT_FQDN },
      tags: { name: 'auth/login' },
    });

    if (res.status !== 200) {
      exec.test.abort(`Connexion refusée pour ${email} (HTTP ${res.status}) : vérifiez le CSV et le tenant.`);
    }

    const body = res.json();
    sessions.push({ email, token: body.accessToken, userId: body.userId, expiresAt: body.expiresAt });
    if (pause) sleep(pause);
  }

  return sessions;
}

/** La session de l'utilisateur virtuel courant : chaque VU garde le même compte tout le test. */
export function sessionOf(sessions) {
  return sessions[(exec.vu.idInTest - 1) % sessions.length];
}
