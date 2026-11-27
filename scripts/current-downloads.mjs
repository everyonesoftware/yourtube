import { discoverWindows, gh, repository } from './publication.mjs';
import { renderTable, replaceTable } from './download-table.mjs';
const write=process.argv.includes('--write');
const found=discoverWindows();
if(!found) { console.log('No verified Windows publication; download table unchanged.'); process.exit(0); }
const table=renderTable(found.manifest);
console.log(table);
if(!write) process.exit(0);

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
console.log('Verified release current-downloads table.');
