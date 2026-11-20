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

test('What’s new groups checked metadata and nests original commit details without any expansion requests', () => {
  assert.match(source, /What’s new · \{count\}/);
  assert.match(source, /AddGroup\("Improvements", changes\.Improvements\)/);
  assert.match(source, /AddGroup\("Fixes", changes\.Fixes\)/);
  assert.match(source, /if \(entries\.Count == 0\) return;/);
  assert.match(source, /notesContent\.Children\.Add\(new Expander \{ Header = "Commit details", Content = details/);
  assert.match(source, /\{changes\.BaseRevision\[\.\.7\]\}…\{changes\.HeadRevision\[\.\.7\]\}/);
  assert.match(source, /\{commit\.Sha\[\.\.7\]\}  \{commit\.Subject\}/);
  assert.match(source, /RetryNotesClicked\([^\n]+=> await flow\.RetryNotesAsync\(\)/);
  assert.doesNotMatch(source, /\.Expanding\s*\+=|\.Expanded\s*\+=|HttpClient|proof of concept/);
});
