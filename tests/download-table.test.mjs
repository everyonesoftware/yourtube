import test from 'node:test';
import assert from 'node:assert/strict';
import { renderTable, replaceTable } from '../scripts/download-table.mjs';
import { selectsWindows } from '../scripts/release-tools.mjs';

test('table uses actual independent published version and preserves surrounding prose',()=>{
 const table=renderTable({version:'0.1.2',tag:'v0.1.2'});
 assert.match(table,/releases\/download\/v0\.1\.2\/YourTubeWindows-win-x64-Setup.exe/);
 assert.match(table,/Setup.exe.sha256/);
 assert.match(table,/Proof of concept/);
 const old='before\n<!-- current-downloads:start -->\nold\n<!-- current-downloads:end -->\nafter';
 const replaced=replaceTable(old,table);
 assert.equal(replaced,`before\n<!-- current-downloads:start -->\n${table}\n<!-- current-downloads:end -->\nafter`);
 assert.equal(replaceTable(replaced,table),replaced);
 assert.throws(()=>replaceTable('no markers',table));
 assert.equal(selectsWindows(['scripts/download-table.mjs']),false);
});
