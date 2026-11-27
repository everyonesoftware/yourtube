import assert from 'node:assert/strict';
import { chromium } from 'playwright';
const file=new URL('index.html',import.meta.url).href;
const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH||'/opt/data/.local/bin/chromium',args:['--no-sandbox']});
try {
 const page=await browser.newPage({viewport:{width:1360,height:960}});
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(process.env.STUDY_URL||file);
 const click=(name,root=page)=>root.getByRole('button',{name,exact:true}).click();
 const app=page.locator('.app');
 for(const layout of ['Library','Theater','Split view']) {
  await click(layout);
  assert.equal(await app.getAttribute('data-layout'),layout);
  await page.screenshot({path:`/opt/data/cache/scratch/yourtube-layout-${layout.toLowerCase().replace(' ','-')}.png`});
 }
 await click('Library');
 await click('The quiet side of Japan',app);
 assert.match(await app.locator('.watch').innerText(),/Up next from Subscriptions/);
 await click('Play',app);
 assert.ok(await app.getByRole('button',{name:'Pause',exact:true}).isVisible());
 await click('Back to Subscriptions',app);
 await click('History',app);
 await click('A night under a million stars',app);
 assert.match(await app.locator('.watch').innerText(),/Up next from History/);
 await click('Demo account',app);await click('Switch to Studio channel',app);
 assert.equal(await app.locator('.watch').count(),0);
 assert.equal(await app.getByRole('button',{name:'Studio channel',exact:true}).count(),1);
 assert.ok(await app.getByRole('button',{name:'A forest walk with no destination',exact:true}).isVisible());
 await click('Studio channel',app);await click('Sign out',app);
 assert.equal(await app.locator('.art').count(),0);
 await click('Connect demo account',app); await click('Cancel',app);
 await click('Connect demo account',app);await click('Simulate authorization',app);
 await click('Settings',app); await click('Check for updates',app);
 assert.match(await app.innerText(),/No update request sent/);
 for(const width of [390,720,1360]) {
  await page.setViewportSize({width,height:960});
  for(const platform of ['Windows','Web','Android']) {
   await click(platform);
   for(const layout of ['Library','Theater','Split view']) {
    await click(layout);
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),`${platform}/${layout}/${width}`);
    assert.equal(await app.getAttribute('data-platform'),platform);
   }
  }
 }
 await click('Android');await click('Library');await click('Reset');await click('Android');
 await page.screenshot({path:'/opt/data/cache/scratch/yourtube-layout-android.png'});
 assert.deepEqual(errors,[]);
 const staticPage=await browser.newPage({javaScriptEnabled:false});await staticPage.goto(file);
 assert.ok(await staticPage.locator('.app h2').isVisible());
 assert.ok((await staticPage.locator('.art').first().evaluate(el=>getComputedStyle(el).backgroundImage)).startsWith('url("data:image/jpeg'));
 console.log('PASS: three layouts, three platform frames, responsive widths, watch/feed navigation, account switching and sign-out, Settings, offline initial render.');
} finally { await browser.close(); }
