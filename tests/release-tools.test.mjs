import test from 'node:test';
import assert from 'node:assert/strict';
import { selectsWindows, validateManifest, sha256, verifyPublishedInventory } from '../scripts/release-tools.mjs';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('native sources and packaging select Windows; docs/mockups/control-plane do not', () => {
  assert.equal(selectsWindows(['apps/windows/YourTube.Core/YouTubeClient.cs']), true);
  assert.equal(selectsWindows(['scripts/build-windows.ps1']), true);
  assert.equal(selectsWindows(['scripts/test-windows-native.ps1']), true);
  assert.equal(selectsWindows(['.github/workflows/ci.yml']), true);
  assert.equal(selectsWindows(['.github/workflows/live-playback.yml']), false);
  assert.equal(selectsWindows(['global.json']), true);
  assert.equal(selectsWindows(['README.md', 'docs/GOOGLE-AUTH.md', 'mockups/foo.html', 'scripts/current-downloads.mjs', 'tests/release-tools.test.mjs']), false);
  assert.equal(selectsWindows(['unknown-build-input']), true);
});

test('real Git publication baseline keeps docs/table changes from rebuilding native binaries',()=>{
 const dir=mkdtempSync(join(tmpdir(),'yourtube-selection-'));
 const git=(...args)=>execFileSync('git',args,{cwd:dir,encoding:'utf8'}).trim();
 try {
  git('init','--initial-branch=main');git('config','user.email','test@example.invalid');git('config','user.name','Fixture');
  writeFileSync(join(dir,'README.md'),'initial');git('add','.');git('commit','-m','baseline');const base=git('rev-parse','HEAD');
  writeFileSync(join(dir,'README.md'),'table refresh');
  mkdirSync(join(dir,'scripts'));
  writeFileSync(join(dir,'scripts/release-changelog.mjs'),'// control-plane producer');
  git('add','.');git('commit','-m','docs and release producer');
  const paths=git('diff','--name-only','--no-renames',`${base}..HEAD`).split('\n');
  assert.equal(selectsWindows(paths),false);
  assert.equal(selectsWindows(['scripts/current-downloads.mjs','scripts/download-table.mjs','scripts/publication.mjs']),false);
 }finally{rmSync(dir,{recursive:true,force:true});}
});

const valid = () => ({schemaVersion:1, platform:'windows', packageId:'YourTubeWindows', channel:'win-x64', version:'0.1.1', tag:'v0.1.1', sourceSha:'a'.repeat(40), assets:[{name:'YourTubeWindows-win-x64-Setup.exe',sha256:sha256(Buffer.from('setup'))},{name:'YourTubeWindows-0.1.1-win-x64-full.nupkg',sha256:sha256(Buffer.from('package'))},{name:'releases.win-x64.json',sha256:sha256(Buffer.from('feed'))}]});

test('only exact canonical complete publication manifests are trusted', () => {
  assert.equal(validateManifest(valid()).version, '0.1.1');
  for (const change of [m=>m.sourceSha='abc',m=>m.tag='v0.1.2',m=>m.version='00.1.1',m=>m.platform='android',m=>m.packageId='MusicWindows',m=>m.assets.pop(),m=>m.assets.push(m.assets[0]),m=>m.assets[0].name='../setup.exe',m=>m.assets[0].sha256='bad']) {
    const manifest=valid(); change(manifest); assert.throws(()=>validateManifest(manifest));
  }
});

test('publication requires real digests and every advertised checksum sidecar',()=>{
 const manifest=valid();
 const prefix='https://github.com/everyonesoftware/yourtube/releases/download/v0.1.1/';
 const release={assets:manifest.assets.flatMap(a=>[{name:a.name,browser_download_url:prefix+a.name,size:100,digest:'sha256:'+a.sha256},{name:a.name+'.sha256',browser_download_url:prefix+a.name+'.sha256',size:100}])};
 assert.equal(verifyPublishedInventory(manifest,release),true);
 const nullDigest=structuredClone(release);nullDigest.assets[0].digest=null;
 assert.throws(()=>verifyPublishedInventory(manifest,nullDigest));
 const missingChecksum=structuredClone(release);missingChecksum.assets.splice(1,1);
 assert.throws(()=>verifyPublishedInventory(manifest,missingChecksum));
 const wrongOrigin=structuredClone(release);wrongOrigin.assets[0].browser_download_url='https://attacker.invalid/setup';
 assert.throws(()=>verifyPublishedInventory(manifest,wrongOrigin));
});
