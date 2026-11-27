import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { manifestName, maxManifestBytes, sha256, validateManifest, verifyPublishedInventory } from './release-tools.mjs';

export const repository = 'everyonesoftware/yourtube';
export function gh(...args) { return execFileSync('gh', args, {encoding:'utf8',maxBuffer:10*1024*1024}); }

function* pages(path) {
 for(let page=1;page<=10;page++) {
  const items=JSON.parse(gh('api',`repos/${repository}/${path}?per_page=100&page=${page}`));
  if(!Array.isArray(items) || items.length>100) throw new Error('Invalid release inventory page');
  yield* items;
  if(items.length<100) return;
 }
 throw new Error('Release inventory truncated');
}

export function resolveTagSource(tag) {
 if(!/^v0\.1\.[1-9]\d{0,4}$/.test(tag)) throw new Error('Invalid Windows release tag');
 let {object}=JSON.parse(gh('api',`repos/${repository}/git/ref/tags/${tag}`));
 const seen=new Set();
 while(object?.type==='tag') {
  if(!/^[a-f0-9]{40}$/.test(object.sha) || seen.has(object.sha) || seen.size>=16) throw new Error('Invalid annotated tag chain');
  seen.add(object.sha);
  const annotated=JSON.parse(gh('api',`repos/${repository}/git/tags/${object.sha}`));
  if(annotated.sha!==object.sha) throw new Error('Annotated tag readback mismatch');
  object=annotated.object;
 }
 if(object?.type!=='commit' || !/^[a-f0-9]{40}$/.test(object.sha)) throw new Error('Release tag does not identify a commit');
 return object.sha;
}

function verifyRelease(release) {
 const manifestAsset=release.assets.filter(a=>a.name===manifestName);
 if(!manifestAsset.length) return null;
 const [asset]=manifestAsset;
 const url=`https://github.com/${repository}/releases/download/${release.tag_name}/${manifestName}`;
 if(manifestAsset.length!==1 || asset.browser_download_url!==url || asset.state!=='uploaded' || !Number.isSafeInteger(asset.size) || asset.size<1 || asset.size>maxManifestBytes || !/^sha256:[a-f0-9]{64}$/.test(asset.digest)) throw new Error('Invalid published Windows manifest asset');
 const dir=mkdtempSync(join(tmpdir(),'yourtube-release-'));
 try {
  gh('release','download',release.tag_name,'--repo',repository,'--pattern',manifestName,'--dir',dir);
  const bytes=readFileSync(join(dir,manifestName));
  if(bytes.length!==asset.size || `sha256:${sha256(bytes)}`!==asset.digest) throw new Error('Published manifest readback mismatch');
  const manifest=validateManifest(JSON.parse(bytes));
  if(manifest.tag!==release.tag_name) throw new Error('Manifest/release tag mismatch');
  if(resolveTagSource(manifest.tag)!==manifest.sourceSha) throw new Error('Release source not verified');
  verifyPublishedInventory(manifest,release);
  gh('release','download',release.tag_name,'--repo',repository,'--pattern','*.sha256','--dir',dir);
  for(const item of manifest.assets) if(readFileSync(join(dir,item.name+'.sha256'),'utf8').trim()!==`${item.sha256}  ${item.name}`) throw new Error('Published checksum content mismatch');
  return {manifest,release};
 } finally { rmSync(dir,{recursive:true,force:true}); }
}

// Selection, downloads and cumulative history share the same publication proof.
export function* windowsPublications() {
 const candidates=[...pages('releases')].filter(r=>r.draft===false && r.prerelease===false && /^v0\.1\.[1-9]\d{0,4}$/.test(r.tag_name));
 candidates.sort((a,b)=>Number(b.tag_name.slice(5))-Number(a.tag_name.slice(5)));
 const tags=new Set();
 for(const release of candidates) {
  if(tags.has(release.tag_name) || !Number.isSafeInteger(release.id) || release.id<1) throw new Error('Invalid or duplicate Windows publication');
  tags.add(release.tag_name);
  const publication=verifyRelease({...release,assets:[...pages(`releases/${release.id}/assets`)]});
  if(publication) yield publication;
 }
}

export function discoverWindows() { return windowsPublications().next().value ?? null; }

export function discoverAndroidDownload() {
 const tagPattern=/^v(0\.1\.([1-9]\d{0,4}))-android$/;
 const candidates=[...pages('releases')].filter(r=>r.draft===false && r.prerelease===false && tagPattern.test(r.tag_name));
 candidates.sort((a,b)=>Number(tagPattern.exec(b.tag_name)[2])-Number(tagPattern.exec(a.tag_name)[2]));
 if(!candidates.length) return null;
 const {id,tag_name:tag}=candidates[0];
 if(!Number.isSafeInteger(id) || id<1 || candidates[1]?.tag_name===tag) throw new Error('Invalid or duplicate Android publication');
 const version=tagPattern.exec(tag)[1], name=`YourTube-${version}.apk`;
 const assets=[...pages(`releases/${id}/assets`)];
 const base=`https://github.com/${repository}/releases/download/${tag}`;
 function requireAsset(filename,limit) {
  const matches=assets.filter(a=>a.name===filename), [asset]=matches;
  if(matches.length!==1 || asset.browser_download_url!==`${base}/${filename}` || asset.state!=='uploaded' || !Number.isSafeInteger(asset.size) || asset.size<1 || asset.size>limit || !/^sha256:[a-f0-9]{64}$/.test(asset.digest)) throw new Error(`Invalid published Android asset: ${filename}`);
  return asset;
 }
 const apk=requireAsset(name,256*1024*1024), checksum=requireAsset(name+'.sha256',1024);
 const dir=mkdtempSync(join(tmpdir(),'yourtube-android-release-'));
 try {
  function readAsset(asset) {
   gh('release','download',tag,'--repo',repository,'--pattern',asset.name,'--dir',dir);
   const bytes=readFileSync(join(dir,asset.name));
   if(bytes.length!==asset.size || `sha256:${sha256(bytes)}`!==asset.digest) throw new Error(`Android asset readback mismatch: ${asset.name}`);
   return bytes;
  }
  if(readAsset(checksum).toString('utf8').trim()!==`${apk.digest.slice(7)}  ${name}`) throw new Error('Published Android checksum content mismatch');
  readAsset(apk);
  return {version,apkUrl:apk.browser_download_url,checksumUrl:checksum.browser_download_url};
 } finally { rmSync(dir,{recursive:true,force:true}); }
}
