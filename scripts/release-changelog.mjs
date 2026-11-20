import { execFileSync } from 'node:child_process';
import { selectsWindows } from './release-tools.mjs';

const shaPattern=/^[a-f0-9]{40}$/;
const versionPattern=/^0\.1\.[1-9]\d{0,4}$/;
const number=version=>Number(version.slice(4));

function identity({version,sha}) {
 if(typeof version!=='string' || !versionPattern.test(version) || number(version)>65534 || typeof sha!=='string' || !shaPattern.test(sha)) {
  throw new Error('Invalid changelog revision');
 }
 return {version,sha};
}

function summarize(subject,paths,isMerge) {
 const relevant=paths.filter(path=>!/^\.(?:github)\//.test(path) && !/^apps\/(?:android|android-tv|web)\//.test(path) && !/(?:^|\/)(?:README|AGENTS|RELEASING|VALIDATION)\.md$/i.test(path));
 if(paths.length && !selectsWindows(relevant)) return {kind:null,text:null};
 const original=subject.replace(/^(?:\[verified\]\s*)+/i,'');
 if(isMerge && /^merge\b/i.test(original)) return {kind:null,text:null};
 const prefix=/^([a-z]+)(?:\([^)]*\))?!?:\s*/i.exec(original);
 const text=(prefix ? original.slice(prefix[0].length) : original).replace(/[\r\0]/g,' ').trim();
 if(!text) return {kind:null,text:null};
 const fix=prefix?.[1].toLowerCase()==='fix' || /^(?:fix|fixes|fixed|repair|resolve)\b/i.test(text);
 return {kind:fix?'fix':'improvement',text};
}

export function buildChangelog({root,head,publications}) {
 const git=(...args)=>execFileSync('git',['--no-replace-objects',...args],{cwd:root,encoding:'utf8',stdio:['ignore','pipe','pipe'],maxBuffer:32*1024*1024});
 if(git('rev-parse','--is-shallow-repository').trim()!=='false') throw new Error('Full Git history required for changelog');
 head=identity(head);
 const seen=new Set();
 const releases=[];
 for(const entry of [...publications,head].map(identity).sort((a,b)=>number(a.version)-number(b.version))) {
  if(number(entry.version)>number(head.version)) throw new Error('Published Windows version is newer than the offered head');
  const base=releases.at(-1);
  if(base?.version===entry.version) throw new Error('Duplicate Windows publication version');
  if(git('cat-file','-t',entry.sha).trim()!=='commit') throw new Error('Changelog source must be a Git commit');
  const count=Number(git('rev-list','--count',entry.sha).trim());
  if(!Number.isSafeInteger(count) || count<1) throw new Error('Invalid Git commit count');
  let commits=[];
  if(base) {
   git('merge-base','--is-ancestor',base.sha,entry.sha);
   const hashes=git('rev-list','--reverse','--topo-order',`${base.sha}..${entry.sha}`).trim().split('\n').filter(Boolean);
   if(hashes.length!==count-base.count || hashes.at(-1)!==entry.sha) throw new Error('Incomplete Git changelog segment');
   commits=hashes.map(sha=>{
    if(!shaPattern.test(sha) || seen.has(sha)) throw new Error('Invalid or duplicate changelog commit');
    seen.add(sha);
    const raw=git('cat-file','commit',sha);
    const start=raw.indexOf('\n\n');
    if(start<0) throw new Error('Invalid Git commit message');
    const subject=raw.slice(start+2).split('\n',1)[0];
    const paths=git('diff-tree','-m','--no-commit-id','--name-only','-r','-z','--no-renames',sha).split('\0').filter(Boolean);
    const isMerge=raw.slice(0,start).split('\n').filter(line=>line.startsWith('parent ')).length>1;
    return {sha,subject,...summarize(subject,paths,isMerge)};
   });
  }
  releases.push({...entry,count,commits});
 }
 return {schema:1,platform:'windows',head,releases};
}
