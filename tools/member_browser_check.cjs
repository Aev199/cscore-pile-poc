/* Real UI regression for two-plane accidental eccentricity and save/open. */
const {chromium}=require('playwright'),{spawn}=require('child_process'),assert=require('assert'),fs=require('fs');
(async()=>{
 const server=spawn(process.env.PYTHON||'python',['-u','workbench_app.py','--no-browser','--runner',process.env.CSCORE_RUNNER]);
 server.stderr.pipe(process.stderr);let browser;
 try{
  const url=await new Promise((resolve,reject)=>{server.stdout.on('data',d=>{const m=String(d).match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});server.on('exit',c=>reject(Error('server '+c)));});
  browser=await chromium.launch({headless:true,args:['--no-sandbox']});const page=await browser.newPage({viewport:{width:1440,height:1000}});
  let result=null;page.on('response',async r=>{if(r.url().includes('/api/job?')){const v=await r.json();if(v.status==='done')result=v.result;}});
  await page.goto(url);await page.locator('#sectionDiagram svg').waitFor();
  await page.locator('#file').setInputFiles('examples/verification/member_length15_manual.json');
  await page.waitForFunction(()=>document.querySelector('#file').value==='');
  await page.locator('#conditions summary').click();
  assert.equal(await page.locator('#eaMode').inputValue(),'sp63');
  assert.equal(await page.locator('#memberLength').inputValue(),'15');
  assert.equal(await page.locator('#n').inputValue(),'-1000');
  assert.equal(await page.locator('#l0y').inputValue(),'15');
  const calculate=async()=>{result=null;await page.locator('#run').click();await page.locator('#results tbody tr').first().or(page.locator('#error:not([hidden])')).first().waitFor({timeout:180000});if(await page.locator('#error').isVisible())throw Error(await page.locator('#error').textContent());assert(result,'Missing job');return result.stations[0];};
  let station=await calculate();assert.equal(station.loads.length,2);assert.equal(station.member_load_preparation.ea_y_mm,25);
  let normal=station.report.checks.filter(c=>c.check_id==='normal_member_strength');assert(normal.every(c=>c.status==='failed'&&c.utilization>7));
  assert((await page.locator('#results').textContent()).includes('eₐy = 25 мм'));
  if(process.env.MEMBER_SCREENSHOT)await page.screenshot({path:process.env.MEMBER_SCREENSHOT,fullPage:true});
  // A separate second moment and an explicitly already included eccentricity.
  await page.locator('#eaMode').selectOption('source');await page.locator('#eccentricity').check();
  await page.locator('summary').filter({hasText:'Вторая плоскость изгиба'}).click();await page.locator('#my').fill('20');
  station=await calculate();assert.equal(station.loads.length,1);
  normal=station.report.checks.filter(c=>c.check_id==='normal_member_strength');assert(Math.abs(normal[0].utilization-6.346466288)<1e-6);
  const saved=page.waitForEvent('download');await page.locator('#save').click();const file=await saved;
  await page.locator('#file').setInputFiles(await file.path());await page.waitForFunction(()=>document.querySelector('#file').value==='');
  assert.equal(await page.locator('#my').inputValue(),'20');assert.equal(await page.locator('#eaMode').inputValue(),'source');
  console.log('Member UI: 15 m fails in both signs, explicit My matches diagnostic, force basis and preparation survive save/open');
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
