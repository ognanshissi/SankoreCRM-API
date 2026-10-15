#!/usr/bin/env node
/**
 * Génère wwwroot/sdk/<version>/forms.min.js à partir de forms.js, et affiche son empreinte SRI.
 *
 *   npm run sdk:minify -- 1.2.1
 *
 * Refuse d'écraser un forms.min.js existant : un build publié est immuable. Les sites l'épinglent
 * avec `integrity`, et le CRM enregistre son empreinte une seule fois (SdkVersionSeeder ne la
 * recalcule jamais) — changer ses octets ferait refuser le script par le navigateur sur chaque
 * site client. Pour livrer un changement, créer un nouveau dossier de version.
 *
 * --force n'existe que pour un build PAS ENCORE déployé, régénéré pendant son développement.
 */
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { minify } from 'terser';

const args = process.argv.slice(2);
const force = args.includes('--force');
const version = args.find((a) => !a.startsWith('--'));

if (!version || !/^\d+\.\d+\.\d+$/.test(version)) {
  console.error('Usage : npm run sdk:minify -- <version> [--force]   (ex. 1.2.1)');
  process.exit(1);
}

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const dir = join(root, 'src', 'Bootstrapper', 'Sankore.Api', 'wwwroot', 'sdk', version);
const sourcePath = join(dir, 'forms.js');
const targetPath = join(dir, 'forms.min.js');

if (!existsSync(sourcePath)) {
  console.error(`Source introuvable : ${sourcePath}`);
  process.exit(1);
}
if (existsSync(targetPath) && !force) {
  console.error(
    `${targetPath} existe déjà. Un build publié ne se régénère pas : créez une nouvelle version.\n`
      + 'Utilisez --force seulement si cette version n\'a jamais été déployée.',
  );
  process.exit(1);
}

const source = readFileSync(sourcePath, 'utf8');

// Le build doit annoncer la version de son dossier : c'est ce qu'il envoie avec chaque lead.
if (!source.includes(`var SDK_VERSION = '${version}';`)) {
  console.error(`forms.js ne déclare pas SDK_VERSION = '${version}'.`);
  process.exit(1);
}

const result = await minify(source, {
  ecma: 5, // le SDK vise les navigateurs des deux dernières années, sans transpilation
  compress: { passes: 2 },
  mangle: true,
  format: {
    comments: false,
    preamble: `/*! Sankore CRM forms.js SDK v${version} | MIT | source lisible : /sdk/${version}/forms.js */`,
  },
});

writeFileSync(targetPath, result.code + '\n');

const bytes = readFileSync(targetPath);
const sri = `sha384-${createHash('sha384').update(bytes).digest('base64')}`;
console.log(`${targetPath}`);
console.log(`  ${Buffer.byteLength(source)} octets -> ${bytes.length} octets`);
console.log(`  integrity="${sri}"`);
