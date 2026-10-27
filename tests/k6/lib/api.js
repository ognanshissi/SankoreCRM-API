import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';
import { API, TENANT_FQDN } from './config.js';

/** Appels refusés faute de droit : comptés à part, ce n'est pas une panne. */
export const forbidden = new Counter('forbidden_calls');

// Un 403 est attendu quand le rôle du compte n'a pas le droit (KYC, WhatsApp…) : il ne doit pas
// compter dans `http_req_failed`. Un 429 y compte, lui — il veut dire que le test mesure la limite
// de débit et non l'application.
const allow403 = http.expectedStatuses({ min: 200, max: 299 }, 403);

function headers(session) {
  return {
    Authorization: `Bearer ${session.token}`,
    'x-tenant-fqdn': TENANT_FQDN,
    'Content-Type': 'application/json',
  };
}

/**
 * Un GET nommé : `name` regroupe les mesures par route (`leads/{id}` plutôt qu'une ligne par
 * identifiant), c'est ce qui rend le résumé lisible.
 */
export function get(session, name, path) {
  const res = http.get(`${API}/${path}`, { headers: headers(session), tags: { name }, responseCallback: allow403 });
  if (res.status === 403) {
    forbidden.add(1, { name });
    return null;
  }
  check(res, { [`${name} 2xx`]: (r) => r.status >= 200 && r.status < 300 });
  return res.status >= 200 && res.status < 300 ? res : null;
}

export function post(session, name, path, body) {
  const res = http.post(`${API}/${path}`, JSON.stringify(body), { headers: headers(session), tags: { name } });
  check(res, { [`${name} 2xx`]: (r) => r.status >= 200 && r.status < 300 });
  return res;
}

/** `{ items: [...] }` → un élément au hasard, ou `undefined`. */
export function pick(res) {
  const items = res?.json('items') ?? [];
  return items.length ? items[Math.floor(Math.random() * items.length)] : undefined;
}
