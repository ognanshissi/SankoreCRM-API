import { group, sleep } from 'k6';
import { ALLOW_WRITES } from './config.js';
import { get, pick, post } from './api.js';

/** Temps de réflexion d'un agent entre deux écrans, en secondes. */
function think(min = 2, max = 6) {
  sleep(min + Math.random() * (max - min));
}

/**
 * Une itération = ce que fait un agent en quelques minutes, dans l'ordre des écrans du front.
 * Environ 15 requêtes et 20 à 40 s de pauses : un compte reste sous 60 requêtes par minute, loin
 * des 200 de la règle `api`. Ne raccourcissez pas les pauses sans ajouter de comptes.
 */
export function agentDay(session) {
  group('ouverture', () => {
    // Ce que l'application charge à chaque ouverture : le profil, puis les compteurs du menu.
    get(session, 'users/auth-context', 'users/auth-context');
    get(session, 'leads (compteur)', `leads?page=1&pageSize=1&status=New&ownerId=${session.userId}`);
    get(session, 'kyc-files (compteur)', 'kyc-files?page=1&pageSize=1');
    get(session, 'my-notifications', 'my-notifications?limit=10&unreadOnly=true');
  });
  think();

  group('ma journée', () => {
    const from = new Date();
    from.setHours(0, 0, 0, 0);
    get(session, 'leads/me/agenda', `leads/me/agenda?from=${encodeURIComponent(from.toISOString())}&days=7`);
    get(session, 'leads/tasks', `leads/tasks?agentId=${session.userId}`);
    get(session, 'workflow/my-steps', 'workflow/instances/my-steps');
  });
  think();

  group('leads', () => {
    const page = get(session, 'leads (liste)', 'leads?page=1&pageSize=25');
    think(1, 3);
    get(session, 'leads (recherche)', `leads?page=1&pageSize=25&search=${['a', 'ko', 'tra', 'ou'][Math.floor(Math.random() * 4)]}`);
    const lead = pick(page);
    if (lead) {
      think(1, 3);
      get(session, 'leads/{id}', `leads/${lead.id}`);
    }
  });
  think();

  group('clients', () => {
    const page = get(session, 'clients (liste)', 'clients?page=1&pageSize=25');
    const client = pick(page);
    if (client) {
      think(1, 3);
      get(session, 'clients/{id}', `clients/${client.id}`);
    }
  });
  think();

  if (ALLOW_WRITES) {
    group('saisie', () => {
      // Un numéro unique par itération : sinon la détection de doublons répond 409 et l'on mesure
      // autre chose que la saisie.
      const suffix = `${Date.now()}${Math.floor(Math.random() * 1000)}`.slice(-8);
      post(session, 'leads (saisie)', 'leads', {
        fullName: `K6 Test ${suffix}`,
        phoneNumber: `+22507${suffix}`,
        source: 'Agency',
        interestedProduct: 'Unknown',
        comment: 'Créé par un test de charge k6',
      });
    });
    think();
  }
}
