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
  // No hidden background audit until the engineer explicitly opens diagnostics.
  assert(!(await page.locator('#advancedQ').getAttribute('open')));

  // Ordinary reinforcement zones and typical pitches are visible; Q solver
  // experiments stay behind one explicitly opened section.
  assert(await page.locator('#qPitchOptions').isVisible());
  assert(await page.locator('#addQZone').isVisible());
  assert(!(await page.locator('#screenQ').isVisible()));
  await page.locator('#qPitchOptions').fill('100; 125; 150; 200; 300');
  await page.locator('#qPitchOptions').dispatchEvent('change');
  assert.equal(await page.locator('#qPitchChoices option').count(),5);
  await page.locator('#advancedQ > summary').click();
  await page.locator('#inspection .metric').filter({hasText:'Согласованность M–Q'}).waitFor({state:'attached'});
  assert(await page.locator('#inspection').textContent().then(s=>s.includes('Диагностика эпюр не подтверждает')));
  assert(await page.locator('#profileCheck').isDisabled());
  await page.locator('#screenQ').check();
  assert(await page.locator('#qFields').isVisible());
  assert(await page.locator('#profileCheck').isEnabled());
  await page.locator('#profileFields > summary').click();
  assert(await page.locator('#profileCheck').isVisible());
  assert(!(await page.locator('#profileAxesConfirmed').isChecked()));
  await page.locator('#profileAxesConfirmed').check();
  await page.locator('#profileOrientation').selectOption('opposite');
  assert(!(await page.locator('#profileAxesConfirmed').isChecked()));
  await page.locator('#profileOrientation').selectOption('same');
  await page.locator('#addQZone').click();
  assert.equal(await page.locator('#qZoneTable .q-zone').count(),1);
  await page.locator('[data-q-zone="0"][data-q-key="bottom"]').fill('-6');
  await page.locator('[data-q-zone="0"][data-q-key="bottom"]').dispatchEvent('change');
  await page.locator('#addQZone').click();
  assert.equal(await page.locator('#qZoneTable .q-zone').count(),2);
  await page.locator('#qConstructive').check();
  await page.locator('[data-q-zone="0"][data-q-key="spacing_mm"]').fill('300');
  await page.locator('[data-q-zone="0"][data-q-key="spacing_mm"]').dispatchEvent('change');
  assert(!(await page.locator('#qConstructive').isChecked()));
  await page.locator('#realTieDetails > summary').click();
  await page.locator('#tieMode').selectOption('specified');
  const realTies=Array.from({length:20},(_,i)=>50+300*i)
      .concat(Array.from({length:40},(_,i)=>6050+150*i));
  await page.locator('#tiePositions').fill(realTies.join('; '));
  await page.locator('#tiePositions').dispatchEvent('change');
  await page.locator('#tieGeometryConfirmed').check();
  const savedQ=page.waitForEvent('download');await page.locator('#save').click();
  const qFile=await savedQ;const qStream=await qFile.createReadStream();
  const qParts=[];for await(const part of qStream)qParts.push(part);
  const qData=JSON.parse(Buffer.concat(qParts).toString());
  assert.equal(qData.transverse_zones.length,2);
  assert.equal(qData.transverse_zones[0].spacing_mm,300);
  assert.equal(qData.transverse_layout.mode,'specified');
  assert.equal(qData.transverse_layout.positions_mm.length,60);
  assert.equal(qData.transverse_layout.geometry_confirmed,true);
  assert.deepEqual(qData.q_allowed_steps_mm,[100,125,150,200,300]);
  // Export draft working zones without issuing engineering approval.
  const scheduleReady=page.waitForEvent('download');
  await page.locator('#exportSchedule').click();
  const scheduleDownload=await scheduleReady;
  assert(scheduleDownload.suggestedFilename().includes('черновик.csv'));
  const scheduleStream=await scheduleDownload.createReadStream();
  const scheduleParts=[];for await(const part of scheduleStream)scheduleParts.push(part);
  const scheduleCsv=Buffer.concat(scheduleParts).toString();
  assert(scheduleCsv.includes('ЧЕРНОВИК, НЕ ПРОВЕРЕНО'));
  await page.locator('#qSpacing').fill('150');
  await page.locator('#qSpacing').dispatchEvent('change');
  assert(!(await page.locator('#qConstructive').isChecked()));
  assert(!(await page.locator('#tieGeometryConfirmed').isChecked()));
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
  assert.equal(await page.locator('#qZoneTable .q-zone').count(),0);
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
  assert.deepEqual(errors,[]);console.log('Browser QA passed: simplified Q editor, hidden advanced diagnostics, draft schedule CSV, Q controls, diagrams and secure project persistence.');
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
