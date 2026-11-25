import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { affectsWindowsWorkflow, selectsWindows } from './release-tools.mjs';
import { discoverWindows } from './publication.mjs';
const prior=discoverWindows();
let windows=true;
if(prior) {
  const baseline=prior.manifest.sourceSha;
  execFileSync('git',['cat-file','-e',`${baseline}^{commit}`]);
  // Fail closed if the published source is not an ancestor of this checkout.
  execFileSync('git',['merge-base','--is-ancestor',baseline,'HEAD']);
  const paths=execFileSync('git',['diff','--name-only','--no-renames',`${baseline}..HEAD`],{encoding:'utf8'}).trim().split('\n').filter(Boolean);
  const workflow='.github/workflows/ci.yml';
  if (paths.includes(workflow)) {
    const read=ref=>execFileSync('git',['show',`${ref}:${workflow}`],{encoding:'utf8',stdio:['ignore','pipe','ignore']});
    try {
      if (!affectsWindowsWorkflow(read(baseline),read('HEAD'))) paths.splice(paths.indexOf(workflow),1);
    } catch { /* missing/unknown workflow: keep the conservative rebuild */ }
  }
  windows=selectsWindows(paths);
}
console.log(JSON.stringify({windows,baseline:prior?.manifest.sourceSha??null}));
if(process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT,`windows=${windows}\n`);
