import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { selectsWindows } from '../scripts/release-tools.mjs';

const selector = fileURLToPath(new URL('../scripts/select-android.mjs', import.meta.url));
const packageScript = fileURLToPath(new URL('../scripts/build-android.sh', import.meta.url));

test('CI selects Android for its real Git push diff, shared fixture, and manual runs', () => {
  const cwd = mkdtempSync(join(tmpdir(), 'yourtube-android-ci-'));
  const git = (...args) => execFileSync('git', args, {cwd, encoding: 'utf8'}).trim();
  const output = join(cwd, 'github-output');
  try {
    git('init', '--initial-branch=main'); git('config', 'user.email', 'test@example.invalid'); git('config', 'user.name', 'Fixture');
    git('commit', '--allow-empty', '-m', 'initial');
    const commit = (path) => {
      const before = git('rev-parse', 'HEAD');
      mkdirSync(join(cwd, path, '..'), {recursive: true}); writeFileSync(join(cwd, path), 'fixture');
      git('add', path); git('commit', '-m', path);
      return before;
    };
    const bin = join(cwd, 'bin'); mkdirSync(bin);
    writeFileSync(join(bin, 'gh'), String.raw`#!/bin/sh
case "$2" in
  */runs\?*) printf '{"workflow_runs":[{"id":42,"head_branch":"main","head_sha":"%s","conclusion":"success"}]}' "$FIXTURE_BASELINE" ;;
  */runs/42/artifacts\?*) printf '{"artifacts":[{"name":"android-unsigned-%s","expired":%s,"size_in_bytes":100}]}' "$FIXTURE_BASELINE" "$FIXTURE_EXPIRED" ;;
  *) exit 2 ;;
esac
`, {mode: 0o755});
    const run = (before, event = 'push', expired = false) => {
      writeFileSync(output, '');
      const env = {...process.env, PATH: `${bin}:${process.env.PATH}`, GITHUB_REPOSITORY: 'everyonesoftware/yourtube',
        FIXTURE_BASELINE: before, FIXTURE_EXPIRED: String(expired), GITHUB_EVENT_NAME: event, GITHUB_OUTPUT: output};
      const {android} = JSON.parse(execFileSync(process.execPath, [selector], {cwd, env, encoding: 'utf8'}));
      assert.equal(readFileSync(output, 'utf8'), `android=${android}\n`);
      return android;
    };
    assert.equal(run(commit('docs/ANDROID.md')), false);
    assert.equal(run(commit('apps/windows/YourTube.App/MainWindow.cs')), false);
    assert.equal(run(commit('apps/android/app/build.gradle.kts')), true);
    assert.equal(run(commit('apps/windows/YourTube.Playback.NativeValidation/Fixtures/audio.mp4')), true);
    assert.equal(run(commit('scripts/build-android.sh')), true);
    assert.equal(run(commit('scripts/release-tools.mjs')), true);
    const beforeQueuedPush = commit('apps/android/pending-change');
    const beforeDocsPush = commit('docs/queued-run-replacement.md');
    assert.equal(run(beforeQueuedPush), true);
    assert.equal(run(beforeDocsPush), false);
    assert.equal(run(beforeDocsPush, 'push', true), true);
    assert.equal(run(git('rev-parse', 'HEAD'), 'workflow_dispatch'), true);
    assert.equal(run('0'.repeat(40)), true);
    assert.equal(run('a'.repeat(40)), true);
    assert.equal(selectsWindows(['scripts/build-android.sh', 'scripts/select-android.mjs']), false);
  } finally { rmSync(cwd, {recursive: true, force: true}); }
});

test('Android CI refuses signing, invalid versions, and unexpected destinations before Gradle', () => {
  const safe = {RELEASE_SIGNING_ENABLED: 'false', RELEASE_VERSION: '0.1.42', RELEASE_ARTIFACT_DIR: 'artifacts/android-ci'};
  for (const [overrides, expected] of [
    [{RELEASE_SIGNING_ENABLED: 'true'}, /must run without signing/],
    [{RELEASE_VERSION: '0.1.042'}, /Invalid Android build version/],
    [{RELEASE_VERSION: '0.1.9999999999'}, /Invalid Android build version/],
    [{RELEASE_ARTIFACT_DIR: 'artifacts/release'}, /Unexpected Android staging path/],
  ]) {
    const result = spawnSync('bash', [packageScript], {env: {...process.env, ...safe, ...overrides}, encoding: 'utf8'});
    assert.equal(result.status, 1);
    assert.match(result.stderr, expected);
  }
});
