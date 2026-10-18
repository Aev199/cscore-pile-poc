/* Optional developer QA: npm install playwright@1.51.1; npx playwright install chromium */
const {chromium}=require('playwright');
const {spawn}=require('child_process');
const assert=require('assert');
const path=require('path');
(async()=>{
 const root=path.resolve(__dirname,'..');
 const server=spawn(process.env.PYTHON||'python',['-u','workbench_app.py','--no-browser',...(process.env.CSCORE_RUNNER?['--runner',process.env.CSCORE_RUNNER]:[])],{cwd:root,env:{...process.env,PYTHONIOENCODING:'utf-8'}});
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

  // Native-check context: lengths are explicit; each support direction belongs
  // only to the selected depth, and invalid input stays in the dialog.
  await page.locator('#connectionSettings').click();
  const nativeZ=Number(await page.locator('#connectionDialog').getAttribute('data-z'));
  assert.equal(await page.locator('#secondOrderMode').inputValue(),'unspecified');
  await page.locator('#secondOrderMode').selectOption('cscore_member');
  await page.locator('#applyConnectionSettings').click();
  await page.waitForSelector('#connectionDialog .dialogerror');
  await page.locator('#nativeL0x').fill('6');await page.locator('#nativeL0y').fill('6');
  await page.locator('#nativePsi').fill('0.5');await page.locator('#nativeMoment').check();
  await page.locator('#nativeAnchorageFactor').fill('1');await page.locator('#nativeAnchorageConfirmed').check();
  await page.locator('#nativeSupportDown').fill('2');await page.locator('#nativeSupportUp').fill('3');
  await page.locator('#applyConnectionSettings').click();
  await page.locator('#connectionDialog').waitFor({state:'hidden'});

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
  assert.equal(qData.calculation_context.second_order_mode,'cscore_member');
  assert.deepEqual(qData.calculation_context.sections,[{z:nativeZ,support_distance_positive_m:2,support_distance_negative_m:3}]);
  // Export draft working zones without issuing engineering approval.
  const scheduleReady=page.waitForEvent('download');
  await page.locator('#exportSchedule').click();
  const scheduleDownload=await scheduleReady;
  assert(scheduleDownload.suggestedFilename().includes('черновик.csv'));
  const scheduleStream=await scheduleDownload.createReadStream();
  const scheduleParts=[];for await(const part of scheduleStream)scheduleParts.push(part);
  const scheduleCsv=Buffer.concat(scheduleParts).toString();
  assert(scheduleCsv.includes('ЧЕРНОВИК, НЕ ПРОВЕРЕНО'));
  const drawingReady=page.waitForEvent('download');
  await page.locator('#exportDrawing').click();
  const drawingFile=await drawingReady;
  assert(drawingFile.suggestedFilename().endsWith('.dxf'));
  const drawingStream=await drawingFile.createReadStream();
  const drawingChunks=[];for await(const part of drawingStream)drawingChunks.push(part);
  const draftDxf=Buffer.concat(drawingChunks).toString();
  assert(draftDxf.includes('AC1015')&&draftDxf.includes('KJ_CAGE'));
  assert(draftDxf.includes('D10/300')&&draftDxf.includes('KJ_NOTES'));
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
  const json=Buffer.concat(chunks).toString();assert(!json.includes('not-to-be-saved'));assert.equal(JSON.parse(json).kind,'pile');assert.equal(JSON.parse(json).calculation_context.sections.length,0);assert.equal(JSON.parse(json).calculation_context.second_order_mode,'unspecified');
  await page.reload();await page.waitForSelector('#charts svg');
  if(process.env.CSCORE_RUNNER){
    // Real browser -> HTTP worker -> native engine -> visible negative result.
    const p=await page.evaluate(async()=>{const token=document.querySelector('meta[name=session]').content;return (await fetch('/api/default',{headers:{'X-Session':token}})).json();});
    p.materials_confirmed=true;p.eccentricity_confirmed=true;
    p.rows=[0,-2].flatMap(z=>[
      {z,case:'U',basis:'ULS',n:-100,m:40,q:50000,m_long:null},
      {z,case:'S',basis:'SLS',n:-80,m:30,q:15,m_long:30}]);
    p.transverse.mode='screen';p.transverse.constructive_confirmed=true;
    p.calculation_context={second_order_mode:'already_in_loads'};
    await page.locator('#file').setInputFiles({name:'native-check.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(p))});
    await page.locator('#elevation').filter({hasText:/^0/}).waitFor();
    await page.locator('#connectionCheck').click();
    await page.locator('#inspection h3').filter({hasText:'Проверки CScore'}).waitFor({timeout:60000});
    assert((await page.locator('#inspection').textContent()).includes('Не пройдено'));
    assert((await page.locator('#inspection').textContent()).includes('Продолжительное раскрытие'));
    await page.screenshot({path:process.env.NATIVE_SCREENSHOT||'/tmp/workbench-native-checks.png',fullPage:true});
    // If rerunning fails, the old positive/negative check list must disappear.
    await page.route('**/api/connection-calculate',route=>route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({error:'test_native_runner_error'})}));
    await page.locator('#connectionCheck').click();
    await page.locator('#message').filter({hasText:'test_native_runner_error'}).waitFor();
    assert.equal(await page.locator('#inspection h3').filter({hasText:'Проверки CScore'}).count(),0);
    await page.unroute('**/api/connection-calculate');
    // Circular pile: the equivalent spiral is explicit, persisted and visible.
    p.kind='pile';p.force_scale='per_member';p.transverse.mode='screen';
    p.rows=p.rows.filter(r=>r.basis==='ULS').map(r=>({...r,n:0,m:40,q:400}));
    p.calculation_context={second_order_mode:'section_only',circular_shear:{
      method:'vniigs_1984_user_spiral',a_mm:80,winding_diameter_mm:670,
      circular_efficiency:.8,efficiency_basis:'Browser test input',confirmed:true}};
    await page.locator('#file').setInputFiles({name:'spiral.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(p))});
    await page.locator('#kind').filter({has:page.locator('option:checked[value="pile"]')}).waitFor();
    await page.locator('#connectionSettings').click();
    assert(await page.locator('#circularShearFields').isVisible());
    assert.equal(await page.locator('#spiralEfficiency').inputValue(),'0.8');
    assert(await page.locator('#spiralConfirmed').isChecked());
    await page.locator('#spiralEfficiency').fill('0.75');
    assert(!(await page.locator('#spiralConfirmed').isChecked()));
    await page.locator('#spiralConfirmed').check();
    await page.locator('#applyConnectionSettings').click();
    await page.locator('#connectionDialog').waitFor({state:'hidden'});
    await page.locator('#connectionCheck').click();
    await page.locator('#inspection').filter({hasText:'По принятой приведённой модели спирали'}).waitFor({timeout:60000});
    assert((await page.locator('#inspection').textContent()).includes('Browser test input'));
    await page.screenshot({path:process.env.SPIRAL_SCREENSHOT||'/tmp/workbench-spiral-checks.png',fullPage:true});
    // Concrete-only circular Q can be selected without entering a spiral coefficient.
    await page.locator('#connectionSettings').click();
    await page.locator('#circularShearMode').selectOption('vniigs_1984_concrete_only');
    assert(await page.locator('#spiralContributionFields').isHidden());
    assert(!(await page.locator('#spiralConfirmed').isChecked()));
    await page.locator('#spiralConfirmed').check();
    await page.locator('#applyConnectionSettings').click();
    const concreteSaved=page.waitForEvent('download');await page.locator('#save').click();
    const concreteDownload=await concreteSaved;const concreteStream=await concreteDownload.createReadStream();
    const concreteChunks=[];for await(const chunk of concreteStream)concreteChunks.push(chunk);
    const concreteProject=JSON.parse(Buffer.concat(concreteChunks).toString());
    assert.equal(concreteProject.calculation_context.circular_shear.method,'vniigs_1984_concrete_only');
    assert.equal(concreteProject.calculation_context.circular_shear.circular_efficiency,null);
    concreteProject.transverse.mode='off';concreteProject.transverse.constructive_confirmed=false;
    for(const row of concreteProject.rows)row.q=100;
    await page.locator('#file').setInputFiles({name:'concrete.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(concreteProject))});
    await page.locator('#connectionCheck').click();
    await page.locator('#inspection h3').filter({hasText:'Проверки CScore'}).waitFor({timeout:60000});
    assert((await page.locator('#inspection').textContent()).includes('Бетонная часть, без вклада навивки'));
    assert(!(await page.locator('#inspection').textContent()).includes('χ ='));
    await page.screenshot({path:process.env.CONCRETE_SCREENSHOT||'/tmp/workbench-concrete-checks.png',fullPage:true});
  }
  await page.reload();await page.waitForSelector('#charts svg');
  await page.screenshot({path:process.env.SCREENSHOT||'/tmp/workbench-initial.png',fullPage:true});
  assert.deepEqual(errors,[]);console.log('Browser QA passed: native CScore context/report/error invalidation, Q editor, draft exports and project persistence.');
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
