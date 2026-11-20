import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { playbackProofVideos, verifyPlaybackProof } from './verify-playback-proof.mjs';

const ownerSha = '337d458a0e06cdee173787e2f21bbf938cceba48';
const approvedDigest = '4fbb4258adb4e1e62d08788ae77fd86a34f12e28ec46011c276eeb34f71b3995';
const evidencePath = 'docs/evidence/windows-playback-owner.json';
// Deliberately enumerate gate-only paths. Any change to app, dependency, harness or packaging inputs requires fresh proof.
const gateOnly = new Set(['.github/workflows/ci.yml', 'docs/WINDOWS.md', evidencePath,
  'scripts/verify-owner-playback.mjs', 'tests/owner-playback-proof.test.mjs', 'tests/workflow.test.mjs']);
const isSha = sha => typeof sha === 'string' && /^[a-f0-9]{40}$/.test(sha);

export function verifyReleaseTree(tested, candidate, cwd = process.cwd()) {
  if (!isSha(tested) || !isSha(candidate)) throw new Error('Invalid source identity');
  const git = (...args) => execFileSync('git', args, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] });
  git('merge-base', '--is-ancestor', tested, candidate);
  const paths = git('diff', '--name-only', '--no-renames', '-z', tested, candidate, '--').split('\0').filter(Boolean);
  if (paths.some(path => !gateOnly.has(path))) throw new Error('Release inputs have changed since owner playback verification');
}

export function verifyOwnerFallback({ approved, hosted, candidate, verifyTree = verifyReleaseTree }) {
  const row = hosted?.results?.[0];
  if (!isSha(candidate) || hosted?.schemaVersion !== 2 || hosted.sourceSha !== candidate || hosted.status !== 'failed' ||
      hosted.native !== true || hosted.adapter !== 'NativePlaybackSource' || hosted.authenticated !== false || hosted.lifecycle !== null ||
      !Array.isArray(hosted.results) || hosted.results.length !== 1 || row?.videoId !== playbackProofVideos[0] ||
      row.status !== 'failed' || row.stage !== 'resolve' || row.reason !== 'unavailable') throw new Error('Hosted failure is not eligible for the owner proof');
  if (createHash('sha256').update(approved).digest('hex') !== approvedDigest) throw new Error('Owner evidence digest mismatch');
  verifyPlaybackProof(JSON.parse(approved), ownerSha);
  verifyTree(ownerSha, candidate);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const [, , evidence, candidate, outcome] = process.argv;
    if (process.argv.length !== 5 || !isSha(candidate) || !['success','failure'].includes(outcome)) throw new Error('Unexpected gate inputs');
    if (execFileSync('git', ['rev-parse', '--verify', 'HEAD'], {encoding:'utf8'}).trim() !== candidate) throw new Error('Unexpected checkout');
    const hosted = JSON.parse(readFileSync(evidence, 'utf8'));
    if (outcome === 'success') {
      verifyPlaybackProof(hosted, candidate);
      console.log('PASS: hosted native playback evidence matches this commit.');
    } else {
      const approved = execFileSync('git', ['show', `${candidate}:${evidencePath}`], {stdio:['ignore','pipe','ignore']});
      verifyOwnerFallback({ approved, hosted, candidate });
      console.log(`PASS: approved owner Windows playback evidence (${ownerSha}) covers unchanged release inputs.`);
    }
  } catch {
    console.error('FAIL: no eligible native playback evidence for the release candidate.');
    process.exitCode = 1;
  }
}
