import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const library = readFileSync(new URL('../apps/windows/YourTube.App/LibraryView.cs', import.meta.url), 'utf8');

test('Home shelves and library cards share the inline publication age and exact-date tooltip', () => {
  assert.match(library, /RenderShelfCard\([^\n]+=> RenderLibraryCard\(/);
  const renderer = library.split('private void RenderLibraryCard(')[1].split('private async Task LoadLibraryImageAsync(')[0];
  assert.match(renderer, /video\.PublishedAge/);
  assert.match(renderer, /video\.PublishedDate/);
  assert.match(renderer, /ToolTipService\.SetToolTip\(ageLabel,/);
  assert.match(renderer, /labels\.Children\.Add\(metadata\)/);
  assert.match(renderer, /var description = [^\n]+publication/);
});
