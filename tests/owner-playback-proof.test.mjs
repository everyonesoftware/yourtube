import assert from 'node:assert/strict';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { verifyOwnerFallback } from '../scripts/verify-owner-playback.mjs';

const approved = readFileSync(new URL('../docs/evidence/windows-playback-owner.json', import.meta.url));
const owner = JSON.parse(approved);
const candidate = 'f'.repeat(40);
const hosted = { schemaVersion: 2, sourceSha: candidate, status: 'failed', native: true, adapter: 'NativePlaybackSource', authenticated: false, lifecycle: null,
  results: [{ videoId: 'jNQXAC9IVRw', status: 'failed', stage: 'resolve', reason: 'unavailable' }] };

test('only the accepted proof bytes and matching hosted resolver failure qualify', () => {
  const args = { approved, hosted, candidate, verifyTree: () => {} };
  assert.doesNotThrow(() => verifyOwnerFallback(args));
  for (const changed of [ { ...hosted, sourceSha: owner.sourceSha }, { ...hosted, status: 'passed' }, { ...hosted, results: [{ ...hosted.results[0], stage: 'seek' }] }, { ...hosted, results: [{ ...hosted.results[0], reason: 'unsafe_resource' }] }, null ])
    assert.throws(() => verifyOwnerFallback({ ...args, hosted: changed }));
  assert.throws(() => verifyOwnerFallback({ ...args, approved: Buffer.concat([approved, Buffer.from(' ')]) }));
  assert.throws(() => verifyOwnerFallback({ ...args, verifyTree: () => { throw Error('changed inputs'); } }));
});

test('real Git comparison accepts only the enumerated release-gate changes', async () => {
  const { verifyReleaseTree } = await import('../scripts/verify-owner-playback.mjs');
  const dir = mkdtempSync(join(tmpdir(), 'yourtube-owner-gate-'));
  const git = (...args) => execFileSync('git', args, { cwd: dir, encoding: 'utf8' }).trim();
  const commit = () => { git('add', '-A'); git('-c','user.name=Fixture','-c','user.email=fixture@example.invalid','-c','commit.gpgsign=false','commit','-qm','fixture'); return git('rev-parse','HEAD'); };
  try {
    git('init','-q'); mkdirSync(join(dir,'apps/windows'),{recursive:true});
    writeFileSync(join(dir,'apps/windows/test.cs'),'baseline'); const base = commit();
    mkdirSync(join(dir,'.github/workflows'),{recursive:true});
    writeFileSync(join(dir,'.github/workflows/ci.yml'),'release gate only'); const allowed = commit();
    assert.doesNotThrow(() => verifyReleaseTree(base, allowed, dir));
    writeFileSync(join(dir,'global.json'),'changed sdk'); const sdk = commit();
    assert.throws(() => verifyReleaseTree(base, sdk, dir));
    rmSync(join(dir,'apps/windows/test.cs')); const removal = commit();
    assert.throws(() => verifyReleaseTree(allowed, removal, dir));
    assert.throws(() => verifyReleaseTree(allowed, base, dir));
  } finally { rmSync(dir,{recursive:true,force:true}); }
});
