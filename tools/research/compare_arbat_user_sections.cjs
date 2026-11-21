/* Reproduce recorded 400x600 user-report inputs through the shipped manual UI.
 * Both interpretations of ARBAT a1=a2=50 are explicit, never fitted.
 * Run from repository root with CSCORE_RUNNER and NODE_PATH set.
 */
const {chromium}=require('playwright');
const {spawn}=require('child_process');
const fs=require('fs');
(async()=>{
 const out=process.env.ARBAT_AUDIT_OUT||'results/arbat-user-sections/lengths-current';fs.mkdirSync(out,{recursive:true});
 const server=spawn(process.env.PYTHON||'python',['-u','workbench_app.py','--no-browser','--runner',process.env.CSCORE_RUNNER]);
 server.stderr.pipe(process.stderr);let browser;
 try{
  const url=await new Promise((resolve,reject)=>{server.stdout.on('data',d=>{const m=String(d).match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});server.on('exit',c=>reject(Error('Server exit '+c)));});
  browser=await chromium.launch({headless:true,args:['--no-sandbox']});
  const page=await browser.newPage({viewport:{width:1440,height:1000}});
  let lastProject,lastJob;
  page.on('request',r=>{if(r.url().endsWith('/api/section-calculate'))lastProject=r.postDataJSON().project;});
  page.on('response',async r=>{if(r.url().includes('/api/job?')){const d=await r.json();if(d.status==='done')lastJob=d;}});
  await page.goto(url);await page.locator('#sectionDiagram svg').waitFor();
  await page.locator('[data-path="wall.panel_width_mm"]').fill('400');
  await page.locator('#conditions summary').click();await page.locator('#secondOrder').selectOption('cscore_member');await page.locator('#psi').fill('1');
  const rows=[];
  for(const [label,cover] of [['axis50',29.5],['surface50_axis62_5',42]])for(const length of [1,10,12,15]){
   lastJob=null;
   await page.locator('[data-path="wall.cover_mm"]').fill(String(cover));
   await page.locator('#l0x').fill(String(length));await page.locator('#l0y').fill(String(length));
   for(const id of ['confirmed','eccentricity','tiesConfirmed'])await page.locator('#'+id).check();
   await page.locator('#run').click();
   await page.locator('#results tbody tr').first().or(page.locator('#error:not([hidden])')).first().waitFor({timeout:180000});
   if(await page.locator('#error').isVisible())throw Error(await page.locator('#error').textContent());
   if(!lastJob)throw Error('Missing completed native response');
   const checks=lastJob.result.stations[0].report.checks,find=id=>checks.find(c=>c.check_id===id);
   const row={geometry:label,length,normal:find('normal_strength').utilization,
    normal_eta:find('normal_member_strength').utilization,normal_status:find('normal_member_strength').status,
    strip:find('shear_strip').utilization,shear:find('shear_inclined').utilization,
    shear_status:find('shear_inclined').status,...find('member_stability').details};
   rows.push(row);
   fs.writeFileSync(`${out}/${label}_L${length}.json`,JSON.stringify({project:lastProject,result:lastJob.result},null,2));
  }
  fs.writeFileSync(`${out}/summary.json`,JSON.stringify(rows,null,2));console.log(JSON.stringify(rows,null,2));
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
