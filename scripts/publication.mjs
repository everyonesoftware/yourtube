import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { validateManifest, verifyPublishedInventory } from './release-tools.mjs';
export const repository = 'everyonesoftware/yourtube';
export function gh(...args) { return execFileSync('gh', args, {encoding:'utf8',maxBuffer:10*1024*1024}); }
export function discoverWindows() {
  const candidates=[];
  for(let page=1;page<=10;page++) {
    const releases=JSON.parse(gh('api',`repos/${repository}/releases?per_page=100&page=${page}`));
    for(const release of releases) {
      if(release.draft || release.prerelease || !/^v0\.1\.[1-9]\d{0,4}$/.test(release.tag_name)) continue;
      if(release.assets.some(a=>a.name==='yourtube-windows.json')) candidates.push(release);
    }
    if(releases.length<100) break;
    if(page===10) throw new Error('Release inventory truncated');
  }
  if(!candidates.length) return null;
  candidates.sort((a,b)=>Number(b.tag_name.split('.')[2])-Number(a.tag_name.split('.')[2]));
  const release=candidates[0];
  const dir=mkdtempSync(join(tmpdir(),'yourtube-release-'));
  try {
    gh('release','download',release.tag_name,'--repo',repository,'--pattern','yourtube-windows.json','--dir',dir);
    const manifest=validateManifest(JSON.parse(readFileSync(join(dir,'yourtube-windows.json'),'utf8')));
    if(manifest.tag!==release.tag_name) throw new Error('Manifest/release tag mismatch');
    const ref=JSON.parse(gh('api',`repos/${repository}/git/ref/tags/${manifest.tag}`));
    if(ref.object.type!=='commit' || ref.object.sha!==manifest.sourceSha) throw new Error('Release source not verified');
    verifyPublishedInventory(manifest,release);
    gh('release','download',release.tag_name,'--repo',repository,'--pattern','*.sha256','--dir',dir);
    for(const item of manifest.assets) if(readFileSync(join(dir,item.name+'.sha256'),'utf8').trim()!==`${item.sha256}  ${item.name}`) throw new Error('Published checksum content mismatch');
    return {manifest,release};
  } finally { rmSync(dir,{recursive:true,force:true}); }
}
