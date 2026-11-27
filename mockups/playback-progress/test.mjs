import assert from 'node:assert/strict';
import {chromium} from 'playwright';
import {pathToFileURL} from 'node:url';

const [url, standalone, screenshots] = process.argv.slice(2);
const browser = await chromium.launch({executablePath: process.env.CHROMIUM_PATH || '/opt/data/.local/bin/chromium', args:['--no-sandbox']});
try {
  const page = await browser.newPage({viewport:{width:1200,height:800}, reducedMotion:'reduce'});
  const errors=[];
  page.on('pageerror', e=>errors.push(e.message));
  await page.goto(url);
  const toggle=page.getByRole('button',{name:'Playback progress'}), close=page.getByRole('button',{name:'Close video'});
  const panel=page.getByRole('region',{name:'Playback progress'}), stage=page.locator('.stage');
  const width=async()=> (await stage.boundingBox()).width;
  const shellWidth=await width();
  assert.equal(await page.locator('[data-state=done]').count(),3);
  assert.match(await page.locator('[aria-current=step]').innerText(),/10\.0 s so far/);
  assert.ok((await toggle.boundingBox()).x<(await close.boundingBox()).x);
  await page.screenshot({path:`${screenshots}/yourtube-progress-flyout.png`,fullPage:true});
  await toggle.click(); assert.ok(!await panel.isVisible()); assert.equal(await width(),shellWidth);
  await page.keyboard.press('Enter'); assert.ok(await panel.isVisible());
  await page.keyboard.press('Escape'); assert.ok(!await panel.isVisible()); assert.ok(await toggle.evaluate(el=>el===document.activeElement));
  await page.getByRole('button',{name:/Side panel/}).click();
  await page.getByRole('button',{name:'Started',exact:true}).click();
  await page.locator('#still').evaluate(img=>img.decode());
  assert.equal(await page.locator('[data-state=done]').count(),5);
  assert.ok(await width()<shellWidth);
  assert.ok((await stage.boundingBox()).x+await width()<=(await panel.boundingBox()).x+1);
  await page.screenshot({path:`${screenshots}/yourtube-progress-side-panel.png`,fullPage:true});
  await page.getByRole('button',{name:'Failed',exact:true}).click();
  assert.match(await page.locator('[data-state=failed]').innerText(),/Load initial audio & video/);
  await page.getByRole('button',{name:'Retry playback'}).click();
  assert.match(await page.locator('[aria-current=step]').innerText(),/Find video streams/);
  await toggle.click();
  await page.waitForFunction(()=>document.querySelector('#summary').textContent==='Playback started');
  await toggle.click(); assert.equal(await page.locator('[data-state=done]').count(),5);
  await close.click(); assert.ok(await page.getByText('Video closed',{exact:true}).isVisible());
  await page.getByRole('button',{name:'Reopen sample video'}).click(); assert.ok(!await panel.isVisible());
  await page.setViewportSize({width:390,height:720});
  for(const name of [/Flyout/,/Side panel/]){
    await page.getByRole('button',{name}).click();
    await page.getByRole('button',{name:'Slow start',exact:true}).click();
    assert.ok(await panel.isVisible());
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
    const bounds=await panel.boundingBox(); assert.ok(bounds.x>=0 && bounds.x+bounds.width<=390);
  }
  await page.goto(pathToFileURL(standalone).href+'?layout=side&state=playing');
  assert.ok(await page.locator('#still').evaluate(async img=>{await img.decode();return img.naturalWidth>0;}));
  assert.equal(await page.locator('[data-state=done]').count(),5);
  const noJS=await browser.newPage({javaScriptEnabled:false});
  await noJS.goto(pathToFileURL(standalone).href);
  assert.ok(await noJS.getByText('3 of 5 steps complete').isVisible());
  assert.deepEqual(errors,[]);
  console.log('PASS: flyout, side panel, status snapshots, retry through startup, keyboard focus, independent close, narrow layout, and standalone HTML.');
} finally { await browser.close(); }
