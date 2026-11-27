import assert from 'node:assert/strict';
import {chromium} from 'playwright';
const url=process.argv[2]??new URL('./index.html',import.meta.url).href;
const browser=await chromium.launch({executablePath:process.env.CHROMIUM_PATH,args:['--no-sandbox']});
try{
const page=await browser.newPage({viewport:{width:1360,height:940}});
const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.goto(url);
const routes={Home:['Search YouTube',10,[4,21]],Subscriptions:['Search subscriptions',8,[]],History:['Search history',8,[21]]};
const ids=()=>page.locator('.result').evaluateAll(nodes=>nodes.map(n=>Number(n.dataset.video)));
const navigate=title=>page.getByRole('navigation',{name:'YourTube pages'}).getByRole('button',{name:title,exact:true}).click();
const query=page.locator('#query');
const search=async text=>{await query.fill(text);await query.press('Enter');};
for(const width of [1360,375]){
 await page.setViewportSize({width,height:940});
 for(const platform of ['web','windows','android','tv']){
  await page.locator(`[data-platform=${platform}]`).click();
  for(const [title,[label,total]] of Object.entries(routes)){
   await navigate(title);
   await page.getByRole('heading',{name:title,exact:true}).waitFor();
   assert.equal((await ids()).length,total);
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth===document.documentElement.clientWidth));
   assert.ok(await page.locator('.result img').evaluateAll(imgs=>imgs.every(i=>i.complete&&i.naturalWidth>0)));
   const heading=await page.locator('#page-title').boundingBox(), field=await page.locator('.searchbar').boundingBox(), refresh=await page.locator('#refresh-page').boundingBox();
   if(width>=1000&&platform!=='android'){
    assert.ok(field.x>heading.x+heading.width&&Math.abs(field.y+field.height/2-heading.y-heading.height/2)<2&&refresh.x>=field.x+field.width,`Header alignment: ${platform}/${title}`);
   }else assert.ok(field.y>=heading.y+heading.height);
   if(platform!=='tv')assert.equal(await query.getAttribute('placeholder'),label);
  }
 }
}
await page.setViewportSize({width:1360,height:940});
await page.locator('[data-platform=windows]').click();
for(const [title,[,,expected]] of Object.entries(routes)){await navigate(title);await search('Lumos');assert.deepEqual(await ids(),expected);}
await navigate('Subscriptions');assert.equal(await query.inputValue(),'Lumos');
await page.getByText('No matches in Subscriptions').waitFor();
await page.getByRole('button',{name:'Clear search',exact:true}).click();assert.equal((await ids()).length,8);
await search('Nvidia');assert.deepEqual(await ids(),[12,13,14]);
await page.getByRole('button',{name:'CBC News',exact:true}).click();assert.deepEqual(await ids(),[14]);
await page.getByRole('button',{name:'All Subscriptions',exact:true}).click();assert.deepEqual(await ids(),[12,13,14]);
await page.locator('#refresh-page').click();assert.deepEqual(await ids(),[12,13,14]);
await page.locator('.result').first().click();await page.locator('#player').waitFor();await page.keyboard.press('Escape');assert.equal(await page.evaluate(()=>document.activeElement.dataset.video),'12');
await page.locator('[data-platform=tv]').click();
await page.locator('#edit-query').focus();await page.keyboard.press('Enter');await page.keyboard.press('ArrowRight');await page.keyboard.press('ArrowDown');await page.keyboard.press('Enter');assert.equal(await page.locator('.tv-value').innerText(),'Nvidiah');
await page.keyboard.press('Escape');assert.deepEqual(await ids(),[12,13,14]);
await page.keyboard.press('Enter');await page.getByRole('button',{name:'Clear text'}).click();await page.getByRole('button',{name:'P',exact:true}).click();await page.getByRole('button',{name:'Search videos'}).click();assert.deepEqual(await ids(),[10,11,12,13,15,16,17]);
assert.deepEqual(errors,[]);
console.log('Passed: header alignment, 3 pages × 4 platforms × 2 widths, scoped results, channel filtering, clear, refresh, preview, and TV D-pad.');
}finally{await browser.close();}
