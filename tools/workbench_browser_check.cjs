/* Optional developer QA: npm install playwright@1.51.1; npx playwright install chromium */
const {chromium}=require('playwright');
const {spawn}=require('child_process');
const assert=require('assert');
const path=require('path');
(async()=>{
 const root=path.resolve(__dirname,'..');
 const server=spawn(process.env.PYTHON||'python',['-u','workbench_app.py','--no-browser'],{cwd:root,env:{...process.env,PYTHONIOENCODING:'utf-8'}});
 // Preserve the actual Python traceback if startup fails on the CI runner.
 let serverErrors='';server.stderr.setEncoding('utf8');server.stderr.on('data',d=>{serverErrors+=d;process.stderr.write(d);});
 let browser;
 try{
  const url=await new Promise((resolve,reject)=>{let all='';server.stdout.on('data',d=>{all+=d;const m=all.match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});server.on('exit',c=>reject(Error('Server exit '+c+(serverErrors?'\n'+serverErrors:''))));setTimeout(()=>reject(Error('Server startup timeout')),10000).unref();});
  browser=await chromium.launch({headless:true,args:['--no-sandbox']});
  const page=await browser.newPage({viewport:{width:1500,height:1050}});let errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.goto(url);await page.waitForSelector('#charts svg');
  assert.equal(await page.locator('#charts svg').count(),5);
  assert(!(await page.locator('#spacingLabel').isVisible()));
  await page.waitForFunction(() => document.querySelector('#inspection').textContent.includes('Согласованность M–Q'));
  assert(await page.locator('#inspection').textContent().then(s=>s.includes('Диагностика эпюр не подтверждает')));

  // Q is visible but deliberately remains unverified until explicitly screened.
  assert(await page.locator('#profileCheck').isDisabled());
  await page.locator('#screenQ').check();
  assert(await page.locator('#qFields').isVisible());
  assert(await page.locator('#profileCheck').isEnabled());
  await page.locator('#profileFields summary').click();
  assert(await page.locator('#profileCheck').isVisible());
  assert(!(await page.locator('#profileAxesConfirmed').isChecked()));
  await page.locator('#profileAxesConfirmed').check();
  await page.locator('#profileOrientation').selectOption('opposite');
  assert(!(await page.locator('#profileAxesConfirmed').isChecked()));
  await page.locator('#profileOrientation').selectOption('same');
  await page.locator('#qSpacing').fill('150');
  await page.locator('#qSpacing').dispatchEvent('change');
  assert(!(await page.locator('#qConstructive').isChecked()));
  await page.locator('#screenQ').uncheck();
  assert(!(await page.locator('#qFields').isVisible()));
  assert(await page.locator('#profileCheck').isDisabled());
  await page.locator('#charts svg').first().click({position:{x:60,y:240}});
  assert.notEqual(await page.locator('#elevation').textContent(),'—');
  await page.locator('#showQ').check();assert.equal(await page.locator('#charts svg').count(),5);
  await page.locator('#showQ').uncheck();
  // Load replacement must discard obsolete zones rather than reject the shorter range.
  await page.locator('#addZone').click();
  const handle=page.locator('[data-boundary="0:top"]').first();const box=await handle.boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width/2,box.y+75,{steps:4});await page.mouse.up();
  assert(Number(await page.locator('[data-zone="0"][data-key="top"]').inputValue())<0);
  await page.locator('#table').click();
  await page.locator('#tableText').fill('z;case;basis;n;m;q;m_long\n0;A;ULS;-100;50;10;\n-2;A;ULS;-100;60;0;');
  await page.locator('#applyTable').click();await page.locator('#tableDialog').waitFor({state:'hidden'});
  assert.equal(await page.locator('#zoneTable tbody tr').count(),0);
  // Invalid values stay visible in the dialog and do not replace the project.
  await page.locator('#table').click();await page.locator('#tableText').fill('0;A;ULS;nan;1;0;');await page.locator('#applyTable').click();
  await page.waitForSelector('#tableDialog .dialogerror');assert(await page.locator('#tableDialog').isVisible());
  await page.locator('#tableDialog [data-close]').click();
  // PLAXIS reconnect starts with no extraction target, passwords not in saved JSON.
  await page.locator('#plaxis').click();assert(await page.locator('#extract').isDisabled());
  await page.locator('#plaxisPassword').fill('not-to-be-saved');await page.locator('#plaxisDialog [data-close]').click();
  await page.locator('#kind').selectOption('pile');assert.equal(await page.locator('#charts svg').count(),3);
  const saved=page.waitForEvent('download');await page.locator('#save').click();const download=await saved;
  const stream=await download.createReadStream();let chunks=[];for await(const chunk of stream)chunks.push(chunk);
  const json=Buffer.concat(chunks).toString();assert(!json.includes('not-to-be-saved'));assert.equal(JSON.parse(json).kind,'pile');
  await page.reload();await page.waitForSelector('#charts svg');
  await page.screenshot({path:process.env.SCREENSHOT||'/tmp/workbench-initial.png',fullPage:true});
  assert.deepEqual(errors,[]);console.log('Browser QA passed: Q controls, phase profile axis confirmation, diagrams, selection, table replacement/errors, pile mode, project download, no secret persistence.');
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
