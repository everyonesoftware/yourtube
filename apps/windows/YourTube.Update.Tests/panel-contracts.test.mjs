import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../YourTube.App/WindowsUpdatesPanel.cs', import.meta.url), 'utf8');
test('compact native header and explicit update actions, honest notes', () => {
  assert.match(source, /Current Version:/);
  assert.match(source, /Check for updates/);
  assert.match(source, /Download update/);
  assert.match(source, /Restart and install/);
  assert.match(source, /What’s new/);
  assert.match(source, /Changelog unavailable/);
  assert.doesNotMatch(source, /release\.Body|releaseBody|WebView|Process\.Start/);
  assert.match(source, /flow\.CheckAsync\(\)/);
  assert.match(source, /flow\.DownloadAsync\(\)/);
  assert.match(source, /flow\.RestartAsync\(\)/);
  assert.match(source, /flow\.Dispose\(\)/);
  assert.match(source, /if \(!disposed\) Render\(\)/);
});
