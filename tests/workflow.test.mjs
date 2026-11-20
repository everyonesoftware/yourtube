import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const yaml=readFileSync(new URL('../.github/workflows/ci.yml',import.meta.url),'utf8');
test('final publisher checks out complete Git history before enriching the retained Windows artifacts',()=>{
 const release=yaml.slice(yaml.indexOf('  release:'));
 assert.match(release,/uses: actions\/checkout@v4\r?\n\s+with:\r?\n\s+fetch-depth: 0/);
 assert.match(release,/run: node scripts\/publish-release\.mjs artifacts\/release/);
});
test('one Windows job, main-only publication, docs-only refresh, and executable validation commands',()=>{
 assert.equal((yaml.match(/^  windows:/gm)??[]).length,1);
 const release=yaml.slice(yaml.indexOf('  release:'));
 assert.match(release,/if:.*github.ref == 'refs\/heads\/main'/);
 assert.match(release,/needs.windows.result == 'success' \|\| needs.windows.result == 'skipped'/);
 assert.match(release,/Publish verified Windows assets\n\s+if: needs.windows.result == 'success'/);
 assert.match(release,/Refresh verified current downloads[\s\S]*run: node scripts\/current-downloads.mjs --write/);
 assert.match(yaml,/dotnet run --project apps\/windows\/validation\/YourTube.App.Validation.csproj/);
 assert.doesNotMatch(yaml,/node --test apps\/windows\/validation/);
 assert.match(yaml,/node --test apps\/windows\/YourTube.Update.Tests\/panel-contracts.test.mjs/);
});

test('installer smoke can finish before live playback, but both must pass before release assets upload',()=>{
 const windows=yaml.slice(yaml.indexOf('\n  windows:\n'),yaml.indexOf('\n  release:\n'));
 assert.match(windows,/dotnet test apps\/windows\/YourTube.Playback.Tests\/YourTube.Playback.Tests.csproj/);
 const proof=windows.indexOf('scripts/test-windows-playback.ps1');
 const packaging=windows.indexOf('scripts/build-windows.ps1');
 const smoke=windows.indexOf('scripts/test-windows-native.ps1');
 const upload=windows.indexOf('name: windows-release');
 assert.ok(packaging>=0&&packaging<smoke&&smoke<proof&&proof<upload);
 const proofStep=windows.slice(windows.lastIndexOf('      - name:',proof),proof);
 assert.doesNotMatch(proofStep,/continue-on-error|\bif:/);
 const uploadStep=windows.slice(windows.lastIndexOf('      - uses:',upload),upload);
 assert.doesNotMatch(uploadStep,/continue-on-error|\bif:/);
 assert.match(windows,/name: windows-playback-evidence[\s\S]*path: artifacts\/native-playback\/evidence/);
});
