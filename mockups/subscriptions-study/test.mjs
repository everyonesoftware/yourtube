import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';

const browser = await chromium.launch({ executablePath: process.env.CHROME_BIN, args: ['--no-sandbox'] });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 850 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const file = new URL('./index.html', import.meta.url);
  await page.goto(process.argv[2] || file.href);
  const exactChannels = () => page.locator('.card').evaluateAll(cards => cards.map(card => card.dataset.videoChannel));
  const columns = [];
  for (const [id, screenshot] of [['inline', 'A-inline-rail'], ['panel', 'B-channel-panel'], ['compact', 'C-compact-rail']]) {
    await page.locator(`.switches [data-layout="${id}"]`).click();
    assert.equal(await page.locator('.app').getAttribute('data-layout'), id);
    assert.equal(await page.locator('.channels button').first().innerText(), 'All Subscriptions');
    assert.equal(await page.getByText('Latest videos from your channels', { exact: true }).count(), 0);
    assert.deepEqual(await exactChannels(), ['nba','nba','nba','nba','cbc','cbc','nbc','cbc']);
    assert.ok(await page.locator('.card img').evaluateAll(async imgs => {
      await Promise.all(imgs.map(img => img.decode()));
      return imgs.every(img => img.naturalWidth > 0);
    }));
    const rail = await page.locator('.channels').boundingBox();
    const feed = await page.locator('.gridpane').boundingBox();
    assert.ok(rail.x + rail.width <= feed.x);
    columns.push(await page.locator('.grid').evaluate(el => getComputedStyle(el).gridTemplateColumns.split(' ').length));
    if (process.env.CAPTURE) await page.screenshot({ path: fileURLToPath(new URL(`./${screenshot}.png`, import.meta.url)) });
    for (const [name, channel, count] of [['CBC News','cbc',3],['NBA','nba',4],['NBC Sports','nbc',1]]) {
      const choice = page.getByRole('button', { name, exact: true });
      await choice.focus();
      await choice.press('Enter');
      assert.deepEqual(await exactChannels(), Array(count).fill(channel));
      assert.equal(await choice.getAttribute('aria-pressed'), 'true');
      assert.equal(await page.locator('#feed-title').innerText(), name);
    }
    await page.getByRole('button', { name: 'All Subscriptions', exact: true }).click();
    assert.equal((await exactChannels()).length, 8);
  }
  assert.ok(columns[2] > columns[0] && columns[2] > columns[1]);
  await page.setViewportSize({ width: 540, height: 650 });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.getByRole('button', { name: 'CBC News', exact: true }).click();
  assert.deepEqual(await exactChannels(), ['cbc','cbc','cbc']);
  assert.deepEqual(errors, []);
  console.log(`PASS: 3 layouts (columns ${columns.join('/')}); artwork, pointer and keyboard filters, reset to All Subscriptions, narrow layout.`);
} finally {
  await browser.close();
}
