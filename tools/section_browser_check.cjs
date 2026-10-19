/* Real browser / existing CScore endpoint: no selection or duplicate depths. */
const {chromium}=require('playwright');
const {spawn}=require('child_process');
const assert=require('assert');
const path=require('path');
(async()=>{
 const server=spawn(process.env.PYTHON||'python',['-u','workbench_app.py','--no-browser',...(process.env.CSCORE_RUNNER?['--runner',process.env.CSCORE_RUNNER]:[])],{cwd:path.resolve(__dirname,'..'),env:{...process.env,PYTHONIOENCODING:'utf-8'}});
 let browser;server.stderr.pipe(process.stderr);
 try{
  const url=await new Promise((resolve,reject)=>{server.stdout.on('data',d=>{const m=String(d).match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});server.on('exit',c=>reject(Error('Server exit '+c)));setTimeout(()=>reject(Error('startup timeout')),10000).unref();});
  browser=await chromium.launch({headless:true,args:['--no-sandbox']});
  const page=await browser.newPage({viewport:{width:1440,height:1000}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await page.locator('#sectionDiagram svg').waitFor();
  assert.equal(await page.locator('h1').textContent(),'Проверка сечения');
  assert.equal(await page.locator('#loads input[type=number]').count(),3);
  assert.equal(await page.locator('#sectionDiagram svg').count(),1);
  await page.locator('#n').fill('-1000');await page.locator('#m').fill('150');await page.locator('#q').fill('100');
  await page.locator('[data-path="wall.panel_width_mm"]').fill('400');
  assert.equal(await page.locator('[data-path="wall.cage_width_mm"]').inputValue(),'300');
  await page.locator('#confirmed').check();await page.locator('#eccentricity').check();await page.locator('#tiesConfirmed').check();
  const requests=[];page.on('request',r=>{if(r.url().endsWith('/api/section-calculate'))requests.push(r.postDataJSON().project);});
  if(process.env.CSCORE_RUNNER){
   await page.locator('#run').click();await page.locator('#results tbody tr').first().waitFor({timeout:180000});
   assert.equal(requests[0].rows.length,1);assert.equal(requests[0].rows[0].n,-2500);assert.equal(requests[0].max_tier,1);
   assert((await page.locator('#results').textContent()).includes('Наклонное сечение · Q'));
   // Every edit must immediately invalidate all displayed coefficients.
   await page.locator('#m').fill('151');assert.equal(await page.locator('#results tbody tr').count(),0);
  }
  // Saved wall files retain the chosen force basis without a second conversion.
  const downloadPromise=page.waitForEvent('download');await page.locator('#save').click();
  const download=await downloadPromise;await page.locator('#file').setInputFiles(await download.path());
  await page.waitForFunction(()=>document.querySelector('#file').value==='');
  assert.equal(await page.locator('#loadBasis').inputValue(),'per_section');
  assert.equal(await page.locator('#n').inputValue(),'-1000');
  assert.equal(await page.locator('[data-path="wall.panel_width_mm"]').inputValue(),'400');
  await page.locator('#kind').selectOption('pile');
  assert.equal(await page.locator('#forceUnits').textContent(),'На одну сваю: N, Q — кН; M — кН·м');
  assert(await page.locator('#spiralConfirmed').isVisible());
  if(process.env.CSCORE_RUNNER){
   await page.locator('#spiralA').fill('85');
   await page.locator('#withSls').check();
   await page.locator('#slsN').fill('-800');await page.locator('#slsM').fill('120');await page.locator('#slsLong').fill('80');
   await page.locator('#conditions summary').click();
   await page.locator('#secondOrder').selectOption('cscore_member');
   await page.locator('#l0x').fill('6');await page.locator('#l0y').fill('6');
   await page.locator('#withInclinedM').check();
   await page.locator('#supportDown').fill('3');await page.locator('#supportUp').fill('3');await page.locator('#anchorage').fill('1');await page.locator('#anchorageConfirmed').check();
   await page.locator('#confirmed').check();await page.locator('#eccentricity').check();await page.locator('#tiesConfirmed').check();await page.locator('#spiralConfirmed').check();
   await page.locator('#run').click();await page.locator('#results tbody tr').first().waitFor({timeout:180000});
   const p=requests.at(-1);assert.equal(p.force_scale,'per_member');assert.equal(p.calculation_context.circular_shear.method,'vniigs_1984_two_legs');assert.equal(p.rows.length,2);assert.equal(new Set(p.rows.map(r=>r.z)).size,1);
   assert((await page.locator('#results').textContent()).includes('Наклонное сечение · Q'));
   assert((await page.locator('#results').textContent()).includes('Раскрытие трещин · длительное'));
   assert((await page.locator('#results').textContent()).includes('Наклонное сечение · M'));
   assert.equal(p.calculation_context.l0_x_m,6);
   assert.equal(p.calculation_context.sections[0].support_distance_negative_m,3);
  }
  if(process.env.SECTION_SCREENSHOT)await page.screenshot({path:process.env.SECTION_SCREENSHOT,fullPage:true});
  assert.deepEqual(errors,[]);console.log('Single-section browser checks passed');
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
