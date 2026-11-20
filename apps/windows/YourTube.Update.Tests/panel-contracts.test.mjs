import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('../YourTube.App/WindowsUpdatesPanel.cs', import.meta.url), 'utf8');
test('native Settings smoke can inject its backend without changing the production default', () => {
  assert.match(source, /public WindowsUpdatesPanel\(Action\? beforeRestart = null\)\s*:\s*this\(beforeRestart, null\)/);
  assert.match(source, /internal WindowsUpdatesPanel\(Action\? beforeRestart, IWindowsUpdateBackend\? backend\)/);
  assert.match(source, /new WindowsUpdateFlow\(backend \?\? new WindowsUpdateBackend\(installedSourceSha: installedSourceSha\), beforeRestart\)/);
});
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
  assert.match(source, /RetryNotesClicked\([^\n]+=> await flow\.RetryNotesAsync\(\)/);
  assert.doesNotMatch(source, /\.Expanding\s*\+=|\.Expanded\s*\+=|HttpClient|LaunchUriAsync|proof of concept/);
});

test('commit details use one seven-character compare box and one per commit, linking full YourTube SHAs', () => {
  assert.match(source, /details\.Children\.Add\(CommitLink\(\$"\{changes\.BaseRevision\[\.\.7\]\}…\{changes\.HeadRevision\[\.\.7\]\}",\s*\$"https:\/\/github\.com\/everyonesoftware\/yourtube\/compare\/\{changes\.BaseRevision\}\.\.\.\{changes\.HeadRevision\}"/);
  assert.match(source, /row\.Children\.Add\(CommitLink\(commit\.Sha\[\.\.7\],\s*\$"https:\/\/github\.com\/everyonesoftware\/yourtube\/commit\/\{commit\.Sha\}"/);
  assert.match(source, /var subject = NoteText\(commit\.Subject\)/);
  assert.match(source, /Grid\.SetColumn\(subject, 1\);\s*row\.Children\.Add\(subject\)/);
  assert.match(source, /private HyperlinkButton CommitLink\(/);
  assert.match(source, /Content = text,\s*NavigateUri = new Uri\(uri\)/);
  assert.match(source, /AutomationProperties\.SetName\(link, accessibleName\)/);
  assert.match(source, /ToolTipService\.SetToolTip\(link, accessibleName\)/);
  assert.match(source, /Compare source range.*on GitHub \(opens in your browser\)/);
  assert.match(source, /View commit.*on GitHub \(opens in your browser\)/);
});

test('commit links keep native focus and use locally scoped Music-style boxes in all themes', () => {
  const resources = source.match(/<StackPanel x:Name="NotesContent"[^>]*>\s*<StackPanel.Resources>([\s\S]*?)<\/StackPanel.Resources>/)?.[1] ?? '';
  assert.ok(resources, 'link resources must be scoped to NotesContent');
  const style = resources.match(/<Style x:Key="CommitLinkStyle" TargetType="HyperlinkButton">([\s\S]*?)<\/Style>/)?.[1] ?? '';
  for (const [property, value] of Object.entries({
    FontFamily: 'Consolas', CornerRadius: '4', BorderThickness: '1',
    IsTabStop: 'True', UseSystemFocusVisuals: 'True',
  })) assert.ok(style.includes(`Property="${property}" Value="${value}"`), property);
  assert.doesNotMatch(style, /Property="Template"/);
  const theme = key => resources.match(new RegExp(`<ResourceDictionary x:Key="${key}">([\\s\\S]*?)</ResourceDictionary>`))?.[1] ?? '';
  for (const [part, color] of Object.entries({ Background: '#2a312c', BorderBrush: '#545e56', Foreground: '#bbd8c1' })) {
    assert.ok(theme('Default').includes(`x:Key="HyperlinkButton${part}" Color="${color}"`), part);
  }
  for (const name of ['Default', 'Light', 'HighContrast']) {
    for (const state of ['', 'PointerOver', 'Pressed', 'Disabled']) {
      for (const part of ['Background', 'Foreground', 'BorderBrush']) {
        assert.ok(theme(name).includes(`x:Key="HyperlinkButton${part}${state}"`), `${name} ${part}${state}`);
      }
    }
  }
  assert.match(source, /Style = \(Style\)notesContent.Resources\["CommitLinkStyle"\]/);
});
