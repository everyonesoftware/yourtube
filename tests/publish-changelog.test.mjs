import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { delimiter, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256 } from '../scripts/release-tools.mjs';

const publisher=fileURLToPath(new URL('../scripts/publish-release.mjs',import.meta.url));
const manifestName='yourtube-windows.json';
const repo='everyonesoftware/yourtube';
const fixtureGh=`#!/usr/bin/env node
const fs=require('node:fs'), p=require('node:path');
const a=process.argv.slice(2), f=JSON.parse(fs.readFileSync(process.env.FIXTURE));
fs.appendFileSync(f.log, JSON.stringify(a)+'\\n');
const json=x=>console.log(JSON.stringify(x));
const missing=message=>{console.error(message);process.exit(1)};
const url=a[1]?.replace('repos/${repo}/','');
if(a[0]==='api') {
 if(url==='releases?per_page=100&page=1') json(f.pageOne);
 else if(url==='releases?per_page=100&page=2') json(f.prior);
 else if(/^releases\\/\\d+\\/assets\\?per_page=100&page=1$/.test(url)) json(f.prior.find(r=>r.id===Number(url.split('/')[1])).assets);
 else if(url?.startsWith('git/ref/tags/')) {
  const tag=url.split('/').at(-1);
  if(tag===f.manifest.tag && !fs.existsSync(f.tag)) missing('HTTP 404');
  if(tag==='v0.1.2') json({object:{type:'tag',sha:'f'.repeat(40)}});
  else json({object:{type:'commit',sha:tag===f.manifest.tag?f.manifest.sourceSha:f.sources[tag]}});
 } else if(url==='git/tags/'+'f'.repeat(40)) json({sha:'f'.repeat(40),object:{type:'commit',sha:f.sources['v0.1.2']}});
 else if(a[1]==='--method' && a[2]==='POST' && a[3]==='repos/${repo}/git/refs') fs.writeFileSync(f.tag,'');
 else if(url==='releases/tags/'+f.manifest.tag) json({tag_name:f.manifest.tag,draft:false,prerelease:false});
 else missing('Unexpected API '+JSON.stringify(a));
} else if(a[0]==='release' && a[1]==='view') {
 if(f.existing) json({tagName:f.manifest.tag,isDraft:false});
 else missing('release not found');
}
else if(a[0]==='release' && a[1]==='create') {
 for(const file of a.slice(a.indexOf('--draft')+1)) fs.copyFileSync(file,p.join(f.upload,p.basename(file)));
} else if(a[0]==='release' && a[1]==='download') {
 const tag=a[2], dest=a[a.indexOf('--dir')+1], pattern=a.includes('--pattern')?a[a.indexOf('--pattern')+1]:null;
 const from=tag===f.manifest.tag?f.upload:p.join(f.history,tag);
 for(const name of fs.readdirSync(from)) if(!pattern || pattern===name || (pattern==='*.sha256' && name.endsWith('.sha256'))) fs.copyFileSync(p.join(from,name),p.join(dest,name));
 if(tag===f.manifest.tag && f.tamper) fs.appendFileSync(p.join(dest,'${manifestName}'),' ');
} else if(a[0]!=='release' || a[1]!=='edit') missing('Unexpected gh '+JSON.stringify(a));
`;

function setup(t,{large=false,tamper=false,invalidOlder=false,first=false,existing=false}={}) {
 const root=mkdtempSync(join(tmpdir(),'yourtube-publish-fixture-'));
 t.after(()=>rmSync(root,{recursive:true,force:true}));
 const git=(...args)=>execFileSync('git',args,{cwd:root,encoding:'utf8',stdio:['pipe','pipe','pipe']}).trim();
 git('init','--initial-branch=main'); git('config','user.email','fixture@example.invalid'); git('config','user.name','Fixture');
 function commit(subject) {
  writeFileSync(join(root,'app.txt'),subject);
  writeFileSync(join(root,'message'),subject+'\n');
  git('add','app.txt'); git('commit','--quiet','-F','message'); return git('rev-parse','HEAD');
 }
 const sources={'v0.1.2':commit('Baseline'),'v0.1.6':commit('fix: First Windows correction')};
 const head=commit(large?'fix: '+'é'.repeat(300000):'feat: Add settings');
 const dir=join(root,'artifacts','release'), history=join(root,'history'), upload=join(root,'upload');
 function packageAt(version,sourceSha,folder) {
  mkdirSync(folder,{recursive:true});
  const tag='v'+version;
  const assets=['YourTubeWindows-win-x64-Setup.exe',`YourTubeWindows-${version}-win-x64-full.nupkg`,'releases.win-x64.json'].map(name=>{
   const content=Buffer.from('fixture '+name);
   const digest=sha256(content);
   writeFileSync(join(folder,name),content);
   writeFileSync(join(folder,name+'.sha256'),`${digest}  ${name}\r\n`);
   return {name,sha256:digest};
  });
  const manifest={schemaVersion:1,platform:'windows',packageId:'YourTubeWindows',channel:'win-x64',version,tag,sourceSha,assets};
  writeFileSync(join(folder,manifestName),JSON.stringify(manifest,null,2));
  return manifest;
 }
 const manifest=packageAt('0.1.20',head,dir);
 const prior=Object.entries(sources).map(([tag,sha],index)=>{
  const folder=join(history,tag);
  const m=packageAt(tag.slice(1),sha,folder);
  const assets=[manifestName,...m.assets.flatMap(a=>[a.name,a.name+'.sha256'])].map(name=>{
   const data=readFileSync(join(folder,name));
   return {name,size:data.length,digest:`sha256:${sha256(data)}`,state:'uploaded',browser_download_url:`https://github.com/${repo}/releases/download/${tag}/${name}`};
  });
  if(invalidOlder && index===0) assets.pop();
  return {id:index+1,tag_name:tag,draft:false,prerelease:false,assets};
 }).reverse();
 mkdirSync(upload);
 writeFileSync(join(root,'gh'),fixtureGh,{mode:0o755});
 const log=join(root,'calls'), fixture=join(root,'fixture.json');
 writeFileSync(fixture,JSON.stringify({manifest,prior:first?[]:prior,sources,history,upload,log,tamper,existing,tag:join(root,'tag'),pageOne:Array.from({length:100},(_,i)=>({tag_name:`ignored-${i}`,draft:true,assets:[]}))}));
 const run=()=>spawnSync(process.execPath,[publisher,'artifacts/release'],{cwd:root,encoding:'utf8',env:{...process.env,GITHUB_SHA:head,PATH:root+delimiter+process.env.PATH,FIXTURE:fixture}});
 const calls=()=>readFileSync(log,'utf8').trim().split('\n').map(line=>JSON.parse(line));
 return {manifest,dir,upload,run,calls};
}

const posix={skip:process.platform==='win32'?'fixture uses a POSIX gh executable shim':false};
test('publisher uploads cumulative verified history in only the staged manifest',posix,t=>{
 const {manifest,dir,upload,run,calls}=setup(t);
 const input=readFileSync(join(dir,manifestName));
 const result=run();
 assert.equal(result.status,0,result.stderr);
 const enriched=JSON.parse(readFileSync(join(upload,manifestName)));
 const {changelog,...identity}=enriched;
 assert.deepEqual(identity,manifest);
 assert.ok(changelog,'the uploaded manifest should embed verified history');
 assert.deepEqual(changelog.releases.map(r=>[r.version,r.count,r.commits.map(c=>c.text)]),[['0.1.2',1,[]],['0.1.6',2,['First Windows correction']],['0.1.20',3,['Add settings']]]);
 assert.deepEqual(changelog.head,{version:manifest.version,sha:manifest.sourceSha});
 assert.deepEqual(readFileSync(join(dir,manifestName)),input);
 for(const {name} of manifest.assets) for(const file of [name,name+'.sha256']) assert.deepEqual(readFileSync(join(upload,file)),readFileSync(join(dir,file)));
 const log=calls();
 const draft=log.find(a=>a[1]==='create');
 const notes=draft[draft.indexOf('--notes')+1];
 assert.match(notes,/## Improvements\n- Add settings/);
 assert.match(notes,/<!-- current-downloads:start -->/);
 assert.match(notes,new RegExp(`- ${manifest.sourceSha.slice(0,7)} feat: Add settings`));
 assert.doesNotMatch(notes,/First Windows correction|Subscribed channels/);
 const older=log.findIndex(a=>a.includes('v0.1.2') && a[1]==='download');
 assert.ok(older>=0 && older<log.findIndex(a=>a[1]==='create'));
 assert.ok(log.some(a=>a[1].endsWith('/git/tags/'+'f'.repeat(40))));
 assert.ok(log.some(a=>a[1]==='edit'));
});

test('history overflow and incomplete older provenance stop before any remote writes',posix,t=>{
 for(const [options,message] of [[{large:true},/1 MiB/],[{invalidOlder:true},/Missing\/ambiguous published asset or checksum/]]) {
  const {run,calls}=setup(t,options);
  const result=run();
  assert.notEqual(result.status,0);
  assert.match(result.stderr,message);
  assert.ok(calls().every(a=>!['POST','create','edit'].some(verb=>a.includes(verb))));
 }
});

test('changed remote enriched manifest bytes prevent finalizing the draft',posix,t=>{
 const {run,calls}=setup(t,{tamper:true});
 const result=run();
 assert.notEqual(result.status,0);
 assert.match(result.stderr,/Remote artifact readback mismatch/);
 assert.ok(calls().some(a=>a[1]==='create'));
 assert.ok(!calls().some(a=>a[1]==='edit'));
});

test('first Windows publication establishes only its own baseline',posix,t=>{
 const {manifest,upload,run}=setup(t,{first:true});
 const result=run();
 assert.equal(result.status,0,result.stderr);
 const {changelog}=JSON.parse(readFileSync(join(upload,manifestName)));
 assert.deepEqual(changelog.releases,[{version:manifest.version,sha:manifest.sourceSha,count:3,commits:[]}]);
});

test('existing published versions are not rewritten to add changelogs',posix,t=>{
 const {run,calls}=setup(t,{existing:true});
 const result=run();
 assert.notEqual(result.status,0);
 assert.match(result.stderr,/Refusing to overwrite an existing release/);
 assert.deepEqual(calls().map(a=>a.slice(0,2)),[['release','view']]);
});
