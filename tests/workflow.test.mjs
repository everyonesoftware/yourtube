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

test('installer smoke gates release upload independently of live YouTube',()=>{
 const windows=yaml.slice(yaml.indexOf('\n  windows:\n'),yaml.indexOf('\n  release:\n'));
 assert.match(windows,/dotnet test apps\/windows\/YourTube.Playback.Tests\/YourTube.Playback.Tests.csproj/);

 const packaging=windows.indexOf('scripts/build-windows.ps1');
 const smoke=windows.indexOf('scripts/test-windows-native.ps1');
 const upload=windows.indexOf('name: windows-release');
 assert.ok(packaging>=0&&packaging<smoke&&smoke<upload);
 assert.doesNotMatch(windows.slice(windows.lastIndexOf('      - name:',smoke),smoke),/continue-on-error|\bif:/);
 assert.doesNotMatch(yaml,/test-windows-playback\.ps1|verify-owner-playback\.mjs|HOSTED_PLAYBACK_OUTCOME/);
 assert.match(windows,/uses: actions\/checkout@v4\s+with:\s+fetch-depth: 0/);
 const uploadStep=windows.slice(windows.lastIndexOf('      - uses:',upload),upload);
 assert.doesNotMatch(uploadStep,/continue-on-error|\bif:/);
});

test('live playback probe is manual, retains failure evidence, and has no release authority',()=>{
 const probe=readFileSync(new URL('../.github/workflows/live-playback.yml',import.meta.url),'utf8');
 assert.match(probe,/^on:\r?\n  workflow_dispatch:\r?\npermissions:/m);
 assert.match(probe,/permissions:\s+contents: read/);
 assert.match(probe,/runs-on: windows-2022/);
 assert.match(probe,/run: .*scripts\/test-windows-playback\.ps1 .*github\.sha/);
 assert.match(probe,/if: always\(\)[\s\S]*name: windows-live-playback-evidence/);
 assert.doesNotMatch(probe,/continue-on-error|publish-release|windows-release|contents: write/);
});

test('controlled Windows smoke runs independently of live YouTube and releases',()=>{
 const smoke=readFileSync(new URL('../.github/workflows/windows-fixtures.yml',import.meta.url),'utf8');
 assert.match(smoke,/^on:\r?\n  workflow_dispatch:\r?\npermissions:/m);
 assert.match(smoke,/permissions:\s+contents: read/);
 assert.match(smoke,/test-windows-playback\.ps1 -Fixture -SourceSha .*github\.sha/);
 assert.match(smoke,/test-windows-settings\.ps1 -SourceSha .*github\.sha/);
 for (const name of ['native-playback-fixture', 'native-settings'])
   assert.match(smoke,new RegExp(`if: always\\(\\)[\\s\\S]*name: ${name}\\r?\\n\\s+path: artifacts/${name}/evidence`));
 assert.doesNotMatch(smoke,/continue-on-error|publish-release|windows-release|contents: write/);
 assert.doesNotMatch(yaml,/windows-fixtures|test-windows-settings\.ps1/);
});
