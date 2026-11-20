import { readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { validateManifest, sha256 } from './release-tools.mjs';
import { gh, repository } from './publication.mjs';
const dir=process.argv[2];
if(!dir) throw new Error('Usage: node scripts/publish-release.mjs RELEASE_DIRECTORY');
const manifest=validateManifest(JSON.parse(readFileSync(join(dir,'yourtube-windows.json'),'utf8')));
if(manifest.sourceSha!==process.env.GITHUB_SHA) throw new Error('Package source is not this workflow commit');
for(const asset of manifest.assets) if(sha256(readFileSync(join(dir,asset.name)))!==asset.sha256) throw new Error('Local artifact digest mismatch');
let existing;
try { existing=JSON.parse(gh('release','view',manifest.tag,'--repo',repository,'--json','tagName,targetCommitish,isDraft')); }
catch(error) { if(!String(error.stderr).includes('release not found')) throw error; }
if(existing) throw new Error('Refusing to overwrite an existing release');
// --target does not retarget an existing Git tag. Pin/check the tag before any publication.
let tag;
try { tag=JSON.parse(gh('api',`repos/${repository}/git/ref/tags/${manifest.tag}`)); }
catch(error) {
 if(!String(error.stderr).includes('404')) throw error;
 gh('api','--method','POST',`repos/${repository}/git/refs`,'-f',`ref=refs/tags/${manifest.tag}`,'-f',`sha=${manifest.sourceSha}`);
 tag=JSON.parse(gh('api',`repos/${repository}/git/ref/tags/${manifest.tag}`));
}
if(tag.object.type!=='commit'||tag.object.sha!==manifest.sourceSha) throw new Error('Existing release tag source mismatch');
const files=['yourtube-windows.json',...manifest.assets.flatMap(a=>[a.name,a.name+'.sha256'])].map(name=>join(dir,name));
const notes='## Improvements\n- Subscribed channels and latest subscription videos in the native Windows app, using the existing selected account.\n- Refresh and Load more for subscription videos.\n\n## Fixes\n- Preserve subscription pagination while filtering unrelated video shelves.\n- Reject stale subscription results after account changes, navigation or sign-out.\n\nAccount-owner authorization and authenticated subscriptions acceptance remain pending.\n\n<!-- current-downloads:start -->\nPending verified publication.\n<!-- current-downloads:end -->\n\n## Commits\n- '+manifest.sourceSha.slice(0,7)+' Add subscribed channels and latest subscription videos';
gh('release','create',manifest.tag,'--repo',repository,'--target',manifest.sourceSha,'--title',`YourTube ${manifest.version}`,'--notes',notes,'--draft',...files);
// Verify remote bytes before making the draft public.
const verify=mkdtempSync(join(tmpdir(),'yourtube-publish-'));
try {
 gh('release','download',manifest.tag,'--repo',repository,'--dir',verify);
 for(const file of files) {
  const name=file.split(/[\\/]/).at(-1);
  if(sha256(readFileSync(join(verify,name)))!==sha256(readFileSync(file))) throw new Error('Remote artifact readback mismatch');
 }
 gh('release','edit',manifest.tag,'--repo',repository,'--draft=false','--prerelease=false','--latest');
 const ref=JSON.parse(gh('api',`repos/${repository}/git/ref/tags/${manifest.tag}`));
 if(ref.object.type!=='commit'||ref.object.sha!==manifest.sourceSha) throw new Error('Published tag source mismatch');
 const release=JSON.parse(gh('api',`repos/${repository}/releases/tags/${manifest.tag}`));
 if(release.draft||release.prerelease||release.tag_name!==manifest.tag) throw new Error('Publication readback failed');
 console.log(JSON.stringify({tag:manifest.tag,sourceSha:manifest.sourceSha,verifiedAssets:files.length,url:release.html_url}));
} finally { rmSync(verify,{recursive:true,force:true}); }
