import { discoverWindows, gh, repository } from './publication.mjs';
import { renderTable, replaceTable } from './download-table.mjs';
const write=process.argv.includes('--write');
const found=discoverWindows();
if(!found) { console.log('No verified Windows publication; download table unchanged.'); process.exit(0); }
const table=renderTable(found.manifest);
console.log(table);
if(!write) process.exit(0);
// The Contents API blob-SHA precondition rejects observable concurrent README changes.
const file=JSON.parse(gh('api',`repos/${repository}/contents/README.md?ref=main`));
const old=Buffer.from(file.content,'base64').toString('utf8');
const next=replaceTable(old,table);
if(next!==old) {
 gh('api','--method','PUT',`repos/${repository}/contents/README.md`,'-f','message=docs: refresh verified Windows download [skip ci]','-f',`sha=${file.sha}`,'-f',`content=${Buffer.from(next).toString('base64')}`,'-f','branch=main');
 const verified=JSON.parse(gh('api',`repos/${repository}/contents/README.md?ref=main`));
 if(Buffer.from(verified.content,'base64').toString('utf8')!==next) throw new Error('README readback failed');
}
// GitHub release body GET/PATCH has no CAS; serialize automation and reject changes visible before writing.
const release=JSON.parse(gh('api',`repos/${repository}/releases/${found.release.id}`));
const updated=replaceTable(release.body??'',table);
if(updated!==release.body) {
 const current=JSON.parse(gh('api',`repos/${repository}/releases/${release.id}`));
 if(current.body!==release.body) throw new Error('Concurrent release description edit');
 gh('api','--method','PATCH',`repos/${repository}/releases/${release.id}`,'-f',`body=${updated}`);
 const checked=JSON.parse(gh('api',`repos/${repository}/releases/${release.id}`));
 if(checked.body!==updated) throw new Error('Release description readback failed');
}
console.log('Verified README and release current-downloads table.');
