// Build label shown in the app ("<commit>[+] · <date> <time>"), so you can tell on the
// glasses which build is loaded. "+" means client-web had uncommitted changes.
import { execSync } from 'node:child_process';

const git = (args) => execSync(`git ${args}`, { stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim();

export function buildLabel(now = new Date()) {
  let commit = 'dev';
  try {
    commit = git('rev-parse --short HEAD') + (git('status --porcelain -- .') ? '+' : '');
  } catch {
    // Not a git checkout: keep "dev".
  }
  const pad = (n) => String(n).padStart(2, '0');
  const date = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
  return `${commit} · ${date} ${pad(now.getHours())}:${pad(now.getMinutes())}`;
}
