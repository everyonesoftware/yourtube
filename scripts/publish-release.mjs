import { execFileSync } from 'node:child_process';
import { copyFileSync, readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { basename, join } from 'node:path';
import { tmpdir } from 'node:os';
import { manifestName, maxManifestBytes, validateManifest, sha256 } from './release-tools.mjs';
import { gh, repository, resolveTagSource, windowsPublications } from './publication.mjs';
import { buildChangelog } from './release-changelog.mjs';
const dir=process.argv[2];
if(!dir || process.argv.length!==3) throw new Error('Usage: node scripts/publish-release.mjs RELEASE_DIRECTORY');
const original=readFileSync(join(dir,manifestName));
if(original.length>maxManifestBytes) throw new Error('Windows manifest exceeds 1 MiB');
const manifest=validateManifest(JSON.parse(original));
const checkout=execFileSync('git',['rev-parse','HEAD'],{encoding:'utf8'}).trim();
if(manifest.sourceSha!==process.env.GITHUB_SHA || manifest.sourceSha!==checkout) throw new Error('Package source is not this workflow commit');
if(Object.hasOwn(manifest,'changelog')) throw new Error('Expected an original Windows packaging manifest');
const workspace=mkdtempSync(join(tmpdir(),'yourtube-publish-'));
try {
 const staged=join(workspace,manifestName);
 const files=[staged];
 for(const {name,sha256:digest} of manifest.assets) {
  const binary=join(workspace,name), checksum=binary+'.sha256';
  copyFileSync(join(dir,name),binary);
  copyFileSync(join(dir,name+'.sha256'),checksum);
  if(sha256(readFileSync(binary))!==digest) throw new Error('Local artifact digest mismatch');
  if(readFileSync(checksum,'utf8').trim()!==`${digest}  ${name}`) throw new Error('Local checksum content mismatch');
  files.push(binary,checksum);
 }
 let existing;
 try { existing=JSON.parse(gh('release','view',manifest.tag,'--repo',repository,'--json','tagName,targetCommitish,isDraft')); }
 catch(error) { if(!String(error.stderr).includes('release not found')) throw error; }
 if(existing) throw new Error('Refusing to overwrite an existing release');
 const head={version:manifest.version,sha:manifest.sourceSha};
 const publications=Array.from(windowsPublications(),({manifest:m})=>({version:m.version,sha:m.sourceSha}));
 const changelog=buildChangelog({head,publications});
 const enriched=Buffer.from(JSON.stringify({...manifest,changelog})+'\n');
 if(enriched.length>maxManifestBytes) throw new Error('Complete Windows manifest exceeds 1 MiB; history cannot be truncated');
 writeFileSync(staged,enriched);
 const expected=new Map(files.map(file=>[basename(file),sha256(readFileSync(file))]));
 // All local and historical evidence is checked before creating a tag or draft.
 let source;
 try { source=resolveTagSource(manifest.tag); }
 catch(error) {
  if(!/HTTP 404\b/.test(String(error.stderr))) throw error;
  gh('api','--method','POST',`repos/${repository}/git/refs`,'-f',`ref=refs/tags/${manifest.tag}`,'-f',`sha=${manifest.sourceSha}`);
  source=resolveTagSource(manifest.tag);
 }
 if(source!==manifest.sourceSha) throw new Error('Existing release tag source mismatch');
 const commits=changelog.releases.at(-1).commits;
 const escape=text=>text.replace(/[\r\0]/g,' ').replace(/[\\`*_\[\]<>@]/g,'\\$&');
 const sections=[['Improvements','improvement'],['Fixes','fix']].flatMap(([label,kind])=>{
  const bullets=commits.filter(commit=>commit.kind===kind).map(commit=>`- ${escape(commit.text)}`);
  return bullets.length ? [`## ${label}\n${bullets.join('\n')}`] : [];
 });
 if(commits.length) sections.push(`## Commits\n${commits.map(commit=>`- ${commit.sha.slice(0,7)} ${escape(commit.subject)}`).join('\n')}`);
 const downloads='<!-- current-downloads:start -->\nPending verified publication.\n<!-- current-downloads:end -->';
 const releaseNotes=sections.join('\n\n');
 const notes=[Buffer.byteLength(releaseNotes)<=32_000 ? releaseNotes : 'See the in-app changelog for the complete history.',downloads].filter(Boolean).join('\n\n');
 gh('release','create',manifest.tag,'--repo',repository,'--target',manifest.sourceSha,'--title',`YourTube ${manifest.version}`,'--notes',notes,'--draft',...files);
 // Verify remote bytes before making the draft public.
 const verify=mkdtempSync(join(tmpdir(),'yourtube-publish-'));
 try {
  gh('release','download',manifest.tag,'--repo',repository,'--dir',verify);
  for(const [name,digest] of expected) {
   if(sha256(readFileSync(join(verify,name)))!==digest) throw new Error('Remote artifact readback mismatch');
  }
  if(resolveTagSource(manifest.tag)!==manifest.sourceSha) throw new Error('Draft tag source mismatch');
  gh('release','edit',manifest.tag,'--repo',repository,'--draft=false','--prerelease=false','--latest');
  if(resolveTagSource(manifest.tag)!==manifest.sourceSha) throw new Error('Published tag source mismatch');
  const release=JSON.parse(gh('api',`repos/${repository}/releases/tags/${manifest.tag}`));
  if(release.draft||release.prerelease||release.tag_name!==manifest.tag) throw new Error('Publication readback failed');
  console.log(JSON.stringify({tag:manifest.tag,sourceSha:manifest.sourceSha,verifiedAssets:files.length,url:release.html_url}));
 } finally { rmSync(verify,{recursive:true,force:true}); }
} finally { rmSync(workspace,{recursive:true,force:true}); }
