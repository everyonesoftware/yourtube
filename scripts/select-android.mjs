import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { selectsAndroid } from './release-tools.mjs';

function findBaseline() {
  const repo = process.env.GITHUB_REPOSITORY ?? '';
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repo)) return null;
  const api = endpoint => JSON.parse(execFileSync('gh', ['api', `repos/${repo}/actions/${endpoint}`], {
    encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 10_000,
  }));
  // Previous pushes can fail, skip Android, or be canceled while queued.
  // A successful run without its exact retained Android artifact is not a baseline.
  const {workflow_runs: runs} = api('workflows/ci.yml/runs?branch=main&status=success&per_page=10');
  for (const run of runs.slice(0, 10)) {
    const sha = run.head_sha;
    if (run.head_branch !== 'main' || run.conclusion !== 'success' || !/^[0-9a-f]{40}$/.test(sha) ||
        !Number.isSafeInteger(run.id) || run.id < 1 || String(run.id) === process.env.GITHUB_RUN_ID) continue;
    try { execFileSync('git', ['merge-base', '--is-ancestor', sha, 'HEAD'], {stdio: 'ignore'}); }
    catch { continue; }
    const name = `android-unsigned-${sha}`;
    const {artifacts} = api(`runs/${run.id}/artifacts?name=${name}`);
    if (artifacts.length === 1 && artifacts[0].name === name && artifacts[0].expired === false &&
        Number.isSafeInteger(artifacts[0].size_in_bytes) && artifacts[0].size_in_bytes > 0) return sha;
  }
  return null;
}

let baseline = null;
if (process.env.GITHUB_EVENT_NAME === 'push') {
  try { baseline = findBaseline(); }
  catch { console.error('Android CI baseline unavailable; selecting a fresh build.'); }
}
const android = !baseline || selectsAndroid(execFileSync('git',
  ['diff', '--name-only', '-z', '--no-renames', baseline, 'HEAD'], {encoding: 'utf8'}).split('\0'));
console.log(JSON.stringify({android, baseline}));
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `android=${android}\n`);
