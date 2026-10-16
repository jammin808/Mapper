// A test package from this folder's node_modules (npm ci), or else from the machine's global packages.
import { execSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

/** @param {string} name */
export async function load(name) {
  try {
    return await import(name);
  } catch {
    const root = execSync('npm root -g', { encoding: 'utf8' }).trim();
    const require = createRequire(join(root, 'noop.js'));
    const mod = await import(pathToFileURL(require.resolve(name)).href);
    // A CommonJS package imported by its file path offers its exports only as the default.
    return Object.keys(mod).length === 1 && mod.default ? mod.default : mod;
  }
}
