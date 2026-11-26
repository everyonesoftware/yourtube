import { createHash } from 'node:crypto';

export const manifestName = 'yourtube-windows.json';
export const maxManifestBytes = 1024 * 1024;

const androidTools = ['scripts/build-android.sh', 'scripts/select-android.mjs'];
const androidFixtures = 'apps/windows/YourTube.Playback.NativeValidation/Fixtures/';
export function selectsAndroid(paths) {
  return paths.some(path => path.startsWith('apps/android/') || androidTools.includes(path) ||
    ['.github/workflows/ci.yml', 'scripts/release-tools.mjs'].includes(path) ||
    ['manifest.mpd', 'audio.mp4', 'video.mp4'].some(name => path === androidFixtures + name));
}

// Ignore only the exact Android CI wiring. Compare every other workflow byte,
// so unknown layouts, shared settings and changes to Windows still rebuild.
export function affectsWindowsWorkflow(before, after) {
  const signCondition = "github.event_name == 'workflow_dispatch' && github.ref == 'refs/heads/main' && inputs.sign_android";
  const signedBlock = [
    `      artifact-name: \${{ ${signCondition} && 'android-signed' || 'android-unsigned' }}-\${{ github.sha }}`,
    '      artifact-path: artifacts/android-ci',
    `      signing-enabled: \${{ ${signCondition} }}`,
    '    secrets:',
    ...['keystore-base64', 'keystore-password', 'key-alias', 'key-password'].map(name =>
      `      ${name}: \${{ ${signCondition} && secrets.ANDROID_${name.toUpperCase().replaceAll('-', '_')} || '' }}`),
    '  windows:',
  ].join('\n');
  const withoutAndroid = text => text
    .replace('  workflow_dispatch:\n    inputs:\n      sign_android:\n        description: Build a signed Android release candidate\n        type: boolean\n        default: false\npermissions:', '  workflow_dispatch:\npermissions:')
    .replace(signedBlock, "      artifact-name: android-unsigned-${{ github.sha }}\n      artifact-path: artifacts/android-ci\n      signing-enabled: false\n  windows:")
    .replace(/^(jobs:\n  validate:\n    runs-on: ubuntu-latest\n)    permissions:\n      contents: read\n      actions: read\n(    outputs:\n      windows: \$\{\{ steps\.select\.outputs\.windows \}\}\n)      android: \$\{\{ steps\.android\.outputs\.android \}\}\n/m, '$1$2')
    .replace(/^      - id: android\n        run: node scripts\/select-android\.mjs\n        env:\n          GH_TOKEN: \$\{\{ github\.token \}\}\n(?=  android:\n)/m, '')
    .replace(/^  android:\n    needs: validate\n    if: needs\.validate\.outputs\.android == 'true'\n    uses: everyonesoftware\/workflows-common\/\.github\/workflows\/build-android-v1\.yml@[0-9a-f]{40}\n    permissions:\n      contents: read\n    with:\n      build-script: scripts\/build-android\.sh\n      version: 0\.1\.\$\{\{ github\.run_number \}\}\n      artifact-name: android-unsigned-\$\{\{ github\.sha \}\}\n      artifact-path: artifacts\/android-ci\n      signing-enabled: (?:true|false)\n(?=  windows:\n)/m, '');
  return withoutAndroid(before) !== withoutAndroid(after);
}

export function selectsWindows(paths) {
  return paths.some(path => {
    if (path.startsWith('apps/android/') || androidTools.includes(path)) return false;
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
