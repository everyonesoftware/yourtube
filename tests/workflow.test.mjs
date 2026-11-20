import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
const yaml=readFileSync(new URL('../.github/workflows/ci.yml',import.meta.url),'utf8');
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
