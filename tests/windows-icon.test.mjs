import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const read = path => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');

test('Windows executable and running window use the deployed YourTube icon', () => {
  const project = read('apps/windows/YourTube.App/YourTube.App.csproj');
  assert.match(project, /<ApplicationIcon>Assets[\\/]YourTube\.ico<\/ApplicationIcon>/);
  assert.match(project, /<Content Remove="Assets[\\/]YourTube\.ico"\s*\/>/);
  assert.match(project, /<None Remove="Assets[\\/]YourTube\.ico"\s*\/>/);
  assert.match(project, /<Content Include="Assets[\\/]YourTube\.ico" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest"\s*\/>/);
  const window = read('apps/windows/YourTube.App/MainWindow.cs');
  assert.match(window, /AppWindow\.SetIcon\(Path\.Combine\(AppContext\.BaseDirectory, "Assets", "YourTube\.ico"\)\)/);
  const packaging = read('scripts/build-windows.ps1');
  assert.match(packaging.slice(packaging.indexOf('function Assert-NativePayload'), packaging.indexOf('function Assert-Feed')), /'Assets[\\/]YourTube\.ico'/);
});
