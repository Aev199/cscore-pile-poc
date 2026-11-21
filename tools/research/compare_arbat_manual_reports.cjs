/* Numeric audit of the shipped manual UI. No resistance formulas or fitted data.
 * Input: extracted original ARBAT tables in docs/research/data/restored-cracks-2026-10-19/arbat-seven-reference.json.
 * Unsupported layouts/second bending components remain explicit exclusions.
 */
const {chromium}=require('playwright'),{spawn}=require('child_process'),fs=require('fs');
(async()=>{
 const out=process.env.ARBAT_AUDIT_OUT||'results/arbat-user-sections/manual-seven-restored';fs.mkdirSync(out,{recursive:true});
 const refs=JSON.parse(fs.readFileSync('docs/research/data/restored-cracks-2026-10-19/arbat-seven-reference.json','utf8'));
 const server=spawn(process.env.PYTHON||'python',['-u','workbench_app.py','--no-browser','--runner',process.env.CSCORE_RUNNER]);server.stderr.pipe(process.stderr);let browser;
 try{
  const url=await new Promise((resolve,reject)=>{server.stdout.on('data',d=>{const m=String(d).match(/http:\/\/127\.0\.0\.1:\d+/);if(m)resolve(m[0]);});server.on('exit',c=>reject(Error('Server exit '+c)));});
  browser=await chromium.launch({headless:true,args:['--no-sandbox']});const rows=[];
  for(const ref of refs){
   if(ref.file.startsWith('400x400')){rows.push(...ref.cases.map(c=>({file:ref.file,case:c.case,status:'UNSUPPORTED_TWO_LAYER_LAYOUT',reference:c})));continue;}
   const page=await browser.newPage();let lastProject,lastJob;
   page.on('request',r=>{if(r.url().endsWith('/api/section-calculate'))lastProject=r.postDataJSON().project;});
   page.on('response',async r=>{if(r.url().includes('/api/job?')){const d=await r.json();if(d.status==='done')lastJob=d;}});
   await page.goto(url);await page.locator('#sectionDiagram svg').waitFor();
   const circle=ref.file.startsWith('d'),head=ref.header;
   const dimension=k=>Number(head.match(new RegExp(k+' = (\\d+) мм'))[1]);
   const a=dimension('a₁'),count=Number(head.match(/S₁ - (\d+)Ж/)[1]),diameter=Number(head.match(/S₁ - \d+Ж(\d+)/)[1]);
   if(circle)await page.locator('#kind').selectOption('pile');
   const set=async(key,v)=>page.locator(`[data-path="${key}"]`).fill(String(v));
   if(circle){await set('pile.diameter_mm',dimension('D'));await set('pile.cover_mm',a-8);await set('pile_background',count);await set('pile_diameter',diameter);}
   else{await set('wall.panel_width_mm',dimension('b'));await set('wall.thickness_mm',dimension('h'));await set('wall.cover_mm',a-8);await page.locator('summary').filter({hasText:'Геометрия каркаса'}).click();await set('wall.end_cover_mm',a-8);for(let i=0;i<2;i++){await set('background.'+i,count);await set('diameters.'+i,diameter);}}
   await page.locator('#withQ').uncheck();await page.locator('#withSls').check();
   await page.locator('#conditions summary').click();await page.locator('#secondOrder').selectOption('cscore_member');await page.locator('#l0x').fill('3');await page.locator('#l0y').fill('3');
   for(const c of ref.cases){
    if(c.mz!==0){rows.push({file:ref.file,case:c.case,status:'UNSUPPORTED_SECOND_BENDING_COMPONENT',reference:c});continue;}
    lastJob=null;
    for(const [id,v] of Object.entries({n:c.n,m:c.my,q:c.qz,slsN:c.n,slsM:c.my,slsLong:c.my*c.psi,psi:c.psi}))await page.locator('#'+id).fill(String(v));
    await page.locator('#confirmed').check();await page.locator('#eccentricity').check();
    await page.locator('#run').click();await page.locator('#results tbody tr').first().or(page.locator('#error:not([hidden])')).first().waitFor({timeout:180000});
    if(await page.locator('#error').isVisible())throw Error(ref.file+'/'+c.case+': '+await page.locator('#error').textContent());
    if(!lastJob)throw Error('Missing completed response');
    const checks=lastJob.result.stations[0].report.checks,find=id=>checks.find(r=>r.check_id===id);
    const row={file:ref.file,case:c.case,status:c.n<0?'RAW_LOADS_COMPRESSION_ECCENTRICITY_NOT_REPRODUCED':'NUMERIC_COMPARISON_GEOMETRY_HYPOTHESIS',reference:c,
     normal:find('normal_member_strength')?.utilization,normal_status:find('normal_member_strength')?.status,
     width_long:find('crack_width_long')?.utilization,width_long_status:find('crack_width_long')?.status,
     width_short:find('crack_width_short')?.utilization,width_short_status:find('crack_width_short')?.status};
    rows.push(row);fs.writeFileSync(`${out}/${ref.file}_T${c.case}.json`,JSON.stringify({project:lastProject,result:lastJob.result},null,2));
   }
   await page.close();
  }
  fs.writeFileSync(`${out}/summary.json`,JSON.stringify(rows,null,2));
  console.log(JSON.stringify(rows.map(({reference,...r})=>({...r,arbat:reference.ratios})),null,2));
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1;});
