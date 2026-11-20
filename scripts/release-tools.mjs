import { createHash } from 'node:crypto';

export const manifestName = 'yourtube-windows.json';
export const maxManifestBytes = 1024 * 1024;

export function selectsWindows(paths) {
  return paths.some(path => {
    if (/^apps\/windows\/YourTube\.(Playback|Settings)\.NativeValidation\//.test(path) ||
        ['scripts/test-windows-playback.ps1', 'scripts/test-windows-settings.ps1'].includes(path)) return false;
    if (path.startsWith('apps/windows/') || ['global.json', '.github/workflows/ci.yml', 'scripts/build-windows.ps1', 'scripts/test-windows-native.ps1'].includes(path)) return true;
    if (['.github/workflows/live-playback.yml', '.github/workflows/windows-fixtures.yml'].includes(path)) return false;
    if (/^(docs\/|mockups\/|tests\/)/.test(path) || ['README.md', 'AGENTS.md', 'THIRD-PARTY-NOTICES.md', '.gitignore', 'package.json', 'package-lock.json', 'scripts/release-tools.mjs', 'scripts/release-changelog.mjs', 'scripts/select-windows.mjs', 'scripts/current-downloads.mjs', 'scripts/publish-release.mjs', 'scripts/publication.mjs', 'scripts/download-table.mjs'].includes(path)) return false;
    return true;
  });
}
export function validateManifest(manifest) {
  const m = manifest;
  if (m?.schemaVersion !== 1 || m.platform !== 'windows' || m.packageId !== 'YourTubeWindows' || m.channel !== 'win-x64' || !/^0\.1\.[1-9]\d{0,4}$/.test(m.version) || Number(m.version.split('.')[2]) > 65534 || m.tag !== `v${m.version}` || !/^[a-f0-9]{40}$/.test(m.sourceSha)) throw new Error('Invalid Windows publication identity');
  const required = ['YourTubeWindows-win-x64-Setup.exe', `YourTubeWindows-${m.version}-win-x64-full.nupkg`, 'releases.win-x64.json'];
  if (!Array.isArray(m.assets) || m.assets.length !== required.length || !required.every(name => m.assets.filter(a => a.name === name && /^[a-f0-9]{64}$/.test(a.sha256)).length === 1)) throw new Error('Incomplete Windows publication inventory');
  return m;
}
export function sha256(bytes) { return createHash('sha256').update(bytes).digest('hex'); }
export function verifyPublishedInventory(manifest, release) {
  const prefix=`https://github.com/everyonesoftware/yourtube/releases/download/${manifest.tag}/`;
  for(const item of manifest.assets) {
    for(const name of [item.name,item.name+'.sha256']) {
      const matching=release.assets.filter(a=>a.name===name);
      if(matching.length!==1 || matching[0].browser_download_url!==prefix+name || matching[0].size<=0) throw new Error('Missing/ambiguous published asset or checksum');
      if(name===item.name && matching[0].digest!==`sha256:${item.sha256}`) throw new Error('Missing or mismatched published asset digest');
    }
  }
  return true;
}
