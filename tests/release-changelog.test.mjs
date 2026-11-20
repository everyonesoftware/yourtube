import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { buildChangelog as build } from '../scripts/release-changelog.mjs';

function repo(t) {
 const root=mkdtempSync(join(tmpdir(),'yourtube-history-'));
 t.after(()=>rmSync(root,{recursive:true,force:true}));
 const git=(...args)=>execFileSync('git',args,{cwd:root,encoding:'utf8',stdio:['ignore','pipe','pipe']}).trim();
 git('init','--initial-branch=main');
 git('config','user.email','test@example.invalid');
 git('config','user.name','Fixture');
 const commit=(subject,path)=>{
  if(path) {
   mkdirSync(dirname(join(root,path)),{recursive:true});
   writeFileSync(join(root,path),subject);
  }
  git('add','-A'); git('commit','--allow-empty','-m',subject);
  return git('rev-parse','HEAD');
 };
 return {root,git,commit};
}
const revision=(number,sha)=>({version:`0.1.${number}`,sha});

test('cumulative segments use Git counts, not workflow version gaps, and include merges',t=>{
 const {root,git,commit}=repo(t);
 const baseline=revision(8,commit('baseline','README.md'));
 const docs=commit('docs: add help','docs/help.md');
 git('switch','-c','side');
 const feature=commit('[verified] feat(windows)!: Add history paging','apps/windows/history');
 git('switch','main');
 const other=commit('feat: Android video UI','apps/android/video');
 git('merge','--no-ff','side','-m','Merge history paging');
 const merge=git('rev-parse','HEAD');
 const middle=revision(17,merge);
 mkdirSync(join(root,'apps/windows'),{recursive:true});
 renameSync(join(root,'apps/android/video'),join(root,'apps/windows/video'));
 const fixSubject='[verified] fix(windows): Preserve pagination';
 const head=revision(42,commit(fixSubject));
 const result=build({root,head,publications:[middle,baseline]});
 assert.ok(result,'embedded history should be produced');
 assert.deepEqual({schema:result.schema,platform:result.platform,head:result.head},{schema:1,platform:'windows',head});
 assert.deepEqual(result.releases.map(({commits,...r})=>r),[{...baseline,count:1},{...middle,count:5},{...head,count:6}]);
 assert.deepEqual(result.releases[0].commits,[]);
 const detailed=result.releases[1].commits;
 assert.deepEqual(new Set(detailed.map(c=>c.sha)),new Set([docs,feature,other,merge]));
 assert.equal(detailed.at(-1).sha,merge);
 assert.deepEqual(detailed.at(-1),{sha:merge,subject:'Merge history paging',kind:null,text:null});
 assert.deepEqual(detailed.find(c=>c.sha===feature),{sha:feature,subject:'[verified] feat(windows)!: Add history paging',kind:'improvement',text:'Add history paging'});
 for(const sha of [docs,other]) assert.deepEqual([detailed.find(c=>c.sha===sha).kind,detailed.find(c=>c.sha===sha).text],[null,null]);
 assert.deepEqual(result.releases[2].commits,[{sha:head.sha,subject:fixSubject,kind:'fix',text:'Preserve pagination'}]);
});


test('original Git subject survives JSON while the summary strips mechanics and control characters',t=>{
 const {root,git,commit}=repo(t);
 const baseline=revision(2,commit('baseline','README.md'));
 const source=commit('placeholder','apps/windows/shared');
 const header=git('cat-file','commit',source).split('\n\n',1)[0];
 const subject='[verified] fix(windows): Keep\rdata\0visible';
 const sha=execFileSync('git',['hash-object','--literally','-w','-t','commit','--stdin'],{cwd:root,input:`${header}\n\n${subject}\n\nA separate body\n`,encoding:'utf8'}).trim();
 const {releases}=JSON.parse(JSON.stringify(build({root,head:revision(10,sha),publications:[baseline]})));
 assert.deepEqual(releases[1].commits,[{sha,subject,kind:'fix',text:'Keep data visible'}]);
});

test('missing, divergent, and contradictory publication mappings are rejected',t=>{
 const {root,git,commit}=repo(t);
 const base=commit('root','README.md');
 git('switch','-c','unrelated');
 const unrelated=commit('not in offered ancestry','unrelated');
 git('switch','main');
 const head=revision(14,commit('offered','apps/windows/source'));
 for(const publications of [[revision(2,'a'.repeat(40))],[revision(2,unrelated)],[revision(2,base),revision(2,unrelated)],[revision(15,base)]]) {
  assert.throws(()=>build({root,head,publications}));
 }
});
