'use strict';
const $=id=>document.getElementById(id), token=document.querySelector('meta[name=session]').content;
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=(v,n=3)=>Number.isFinite(v)?v.toLocaleString('ru-RU',{maximumFractionDigits:n}):'—';
let project,busy=false,lastResult=null;
async function api(path,data){const r=await fetch('/api/'+path,{method:data===undefined?'GET':'POST',headers:{'X-Session':token,'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});const v=await r.json();if(!r.ok)throw Error(v.error||'Ошибка запроса');return v;}
function error(text=''){ $('error').textContent=text;$('error').hidden=!text; }
function invalidate(){lastResult=null;$('resultBadge').textContent='Нужен расчёт';$('results').innerHTML='<div class="empty">Исходные данные изменены.<br>Нажмите «Рассчитать» для обновления результата.</div>';}
function getPath(path){return path.split('.').reduce((a,k)=>a[k],project);}
function setPath(path,value){const keys=path.split('.'),last=keys.pop();keys.reduce((a,k)=>a[k],project)[last]=value;}
function field(path,label,{factor=1,min,step='any'}={}){return `<label>${esc(label)}<input type="number" data-path="${esc(path)}" data-factor="${factor}" value="${esc(getPath(path)/factor)}" step="${step}" ${min===undefined?'':`min="${min}"`} required></label>`;}
function n(id,optional=false){const el=$(id);if(optional&&el.value.trim()==='')return null;const value=Number(el.value);if(el.value.trim()===''||!Number.isFinite(value))throw Error('Заполните поле «'+el.closest('label').firstChild.textContent.trim()+'»');return value;}
function renderGeometry(){
 const wall=project.kind==='wall',kind=project.kind;
 $('geometryFields').innerHTML=wall?field('wall.panel_width_mm','Ширина захватки b, мм',{min:1})+field('wall.thickness_mm','Толщина h, мм',{min:1})+field('wall.cover_mm','Защитный слой c, мм',{min:0}):field('pile.diameter_mm','Диаметр D, мм',{min:1})+field('pile.cover_mm','Защитный слой c, мм',{min:0});
 $('bars').innerHTML=wall?[0,1].map(i=>field('background.'+i,`Грань y${i?'+':'−'} · стержней на каркас`,{min:2,step:1})+field('diameters.'+i,'Диаметр продольных стержней, мм',{min:1})).join(''):field('pile_background','Продольных стержней в кольце',{min:6,step:1})+field('pile_diameter','Диаметр продольных стержней, мм',{min:1});
 $('geometryAdvanced').innerHTML=(wall?field('wall.cage_count','Число каркасов',{min:1,step:1})+field('wall.cage_width_mm','Ширина каркаса, мм',{min:1})+field('wall.end_cover_mm','Торцевой защитный слой, мм',{min:0})+field('wall.min_cage_gap_mm','Минимальный зазор каркасов, мм',{min:0}):'')+field(kind+'.min_clear_spacing_mm','Минимальный просвет стержней, мм',{min:1});
 $('transverseTitle').textContent=wall?'Поперечное армирование':'Спиральная навивка';
 $('transverseFields').innerHTML=field('transverse.diameter_mm',wall?'Диаметр хомута, мм':'Диаметр навивки, мм',{min:1})+field('transverse.spacing_mm',wall?'Шаг хомутов, мм':'Полный шаг витка, мм',{min:1})+(wall?field('transverse.effective_legs_per_cage','Рабочих ветвей на каркас',{min:1,step:1}):'')+field('transverse.rsw_kpa','Rsw поперечной стали, МПа',{min:.001,factor:1000});
 $('forceUnits').textContent=wall?'На метр стены: N, Q — кН/м; M — кН·м/м':'На одну сваю: N, Q — кН; M — кН·м';
 draw();toggles();
}
const materialLabels={concrete_rb_kpa:'Rb бетона, МПа',concrete_rbt_kpa:'Rbt бетона, МПа',concrete_e_kpa:'Eb бетона, МПа',rebar_rs_kpa:'Rs продольной стали, МПа',rebar_e_kpa:'Es стали, МПа',concrete_ec1:'εb1',concrete_ec2:'εb2',concrete_et1:'εbt1',concrete_et2:'εbt2',rebar_ec2:'εs−',rebar_et2:'εs+'};
function matField(prefix,key){return field(prefix+'.'+key,materialLabels[key]||key,{factor:key.endsWith('_kpa')?1000:1});}
function renderMaterials(){
 const basic=['concrete_rb_kpa','concrete_rbt_kpa','rebar_rs_kpa'];
 $('materialFields').innerHTML=basic.map(k=>matField('materials',k)).join('');
 $('materialAdvanced').innerHTML=Object.keys(project.materials).filter(k=>!basic.includes(k)).map(k=>matField('materials',k)).join('');
 $('slsMaterials').innerHTML=['short_materials','long_materials'].map((key,i)=>'<h3>'+(i?'Длительная диаграмма SLS':'Кратковременная диаграмма SLS')+'</h3><div class="fields">'+Object.keys(project.sls_settings[key]).map(k=>matField('sls_settings.'+key,k)).join('')+'</div>').join('')+'<h3>Параметры раскрытия трещин CScore</h3><div class="fields">'+['phi1','phi2','phi3','ls_min_mm','ls_max_mm'].filter(k=>k in project.sls_settings).map(k=>field('sls_settings.'+k,k)).join('')+'</div>';
}
function draw(){
 const wall=project.kind==='wall',g=project[project.kind],tie=project.transverse.diameter_mm;
 let shape='',dots='';
 if(wall){
  const w=g.panel_width_mm,h=g.thickness_mm,scale=125/Math.max(w,h),left=(160-w*scale)/2,top=(155-h*scale)/2;
  shape=`<rect x="${left}" y="${top}" width="${w*scale}" height="${h*scale}" rx="2" fill="#edf1ee" stroke="#8da79b"/>`;
  const cages=g.cage_count,cw=g.cage_width_mm,gap=cages>1?(w-2*g.end_cover_mm-cages*cw)/(cages-1):0;
  if(cages<=10&&project.background.every(v=>v>=2&&v<=32))for(let c=0;c<cages;c++)for(let i=0;i<2;i++){
   const count=project.background[i],d=project.diameters[i],inset=tie+d/2;
   for(let j=0;j<count;j++){const start=cages===1?(w-cw)/2:g.end_cover_mm+c*(cw+gap),x=start+inset+j*(cw-2*inset)/(count-1),y=i?g.cover_mm+inset:h-g.cover_mm-inset;
    dots+=`<circle cx="${left+x*scale}" cy="${top+y*scale}" r="${Math.max(2,d*scale/2)}" fill="#256b59"/>`;}
  }
  shape+=`<text x="80" y="${top+h*scale+16}" text-anchor="middle">b = ${esc(w)}</text><text x="80" y="${Math.max(10,top-9)}" text-anchor="middle">y+ · M &lt; 0</text>`;
 }else{
  const d=g.diameter_mm,radius=d/2-g.cover_mm-tie-project.pile_diameter/2;
  shape='<circle cx="80" cy="78" r="61" fill="#edf1ee" stroke="#8da79b"/>';
  if(project.pile_background>=6&&project.pile_background<=32)for(let i=0;i<project.pile_background;i++){const a=i/project.pile_background*2*Math.PI;dots+=`<circle cx="${80+radius/d*122*Math.cos(a)}" cy="${78+radius/d*122*Math.sin(a)}" r="${Math.max(2,project.pile_diameter/d*61)}" fill="#256b59"/>`;}
  shape+=`<text x="80" y="158" text-anchor="middle">D = ${esc(d)} мм</text>`;
 }
 $('sectionDiagram').innerHTML='<svg viewBox="0 0 160 180" role="img" aria-label="Схема расположения продольных стержней" font-size="10" fill="#61756c">'+shape+dots+'</svg>';
}
function toggles(){
 const q=$('withQ').checked,sls=$('withSls').checked,eta=$('secondOrder').value==='cscore_member',inclined=$('withInclinedM').checked;
 $('spiralFields').hidden=project.kind!=='pile'||!q;$('tiesLabel').hidden=!q;
 $('slsLoads').hidden=!sls;$('lengths').hidden=!eta;$('inclinedFields').hidden=!inclined;
 for(const id of ['slsN','slsM','slsLong','limitLong','limitShort'])$(id).required=sls;
 for(const id of ['l0x','l0y','psi'])$(id).required=eta;
 for(const id of ['supportDown','supportUp','anchorage','anchorageConfirmed'])$(id).required=inclined;
 $('tiesConfirmed').required=q;$('spiralConfirmed').required=project.kind==='pile'&&q;$('spiralA').required=project.kind==='pile'&&q;
 $('eccentricity').required=$('secondOrder').value!=='section_only';
 // Hidden optional inputs must not prevent HTML validation of a strength-only check.
 for(const host of ['slsLoads','lengths','inclinedFields','spiralFields'])$(host).querySelectorAll('input').forEach(e=>e.disabled=$(host).hidden);
}
function build(){
 const p=structuredClone(project),wall=p.kind==='wall';
 p.force_scale=wall?'per_metre':'per_member';p.moment_sign=1;p.max_tier=1;p.zones=[];p.transverse_zones=[];p.transverse_layout={mode:'proposed',positions_mm:[],geometry_confirmed:false};
 p.title='Ручная проверка '+(wall?'СВГ':'БНС');p.source={type:'manual'};
 p.materials_confirmed=$('confirmed').checked;p.eccentricity_confirmed=$('eccentricity').checked;
 p[p.kind].ties_diameter_mm=p.transverse.diameter_mm;
 p.transverse.mode=$('withQ').checked?'screen':'off';p.transverse.constructive_confirmed=$('tiesConfirmed').checked;
 p.rows=[{z:0,case:'ULS',basis:'ULS',n:n('n'),m:n('m'),q:n('q'),m_long:null}];
 if($('withSls').checked){p.rows.push({z:0,case:'SLS',basis:'SLS',n:n('slsN'),m:n('slsM'),q:0,m_long:n('slsLong')});p.sls_settings.acrc_ult_long_mm=n('limitLong');p.sls_settings.acrc_ult_short_mm=n('limitShort');}
 const inclined=$('withInclinedM').checked;
 p.calculation_context={second_order_mode:$('secondOrder').value,l0_x_m:$('secondOrder').value==='cscore_member'?n('l0x'):null,l0_y_m:$('secondOrder').value==='cscore_member'?n('l0y'):null,psi:$('secondOrder').value==='cscore_member'?n('psi'):1,inclined_moment_requested:inclined,anchorage_confirmed:inclined&&$('anchorageConfirmed').checked,anchorage_factor:inclined?n('anchorage'):null,sections:inclined?[{z:0,support_distance_positive_m:n('supportDown'),support_distance_negative_m:n('supportUp')}]:[],circular_shear:!wall&&$('withQ').checked?{method:'vniigs_1984_two_legs',a_mm:n('spiralA'),winding_diameter_mm:null,circular_efficiency:null,efficiency_basis:'Принятая модель: один полный виток — две условные ветви; b = 0,885D, h0 = b − a.',confirmed:$('spiralConfirmed').checked}:null};
 return p;
}
const names={normal_strength:'Нормальное сечение · N/M',normal_member_strength:'N/M с учётом второго порядка',shear_strip:'Бетонная полоса · Q',shear_inclined:'Наклонное сечение · Q',inclined_moment:'Наклонное сечение · M',crack_formation:'Образование трещин',crack_width_long:'Раскрытие трещин · длительное',crack_width_short:'Раскрытие трещин · кратковременное',member_stability:'Второй порядок · ULS',sls_member_stability:'Второй порядок · SLS',anchorage:'Длина анкеровки',detailing:'Конструктивные требования'};
const statuses={passed:'Пройдено',failed:'Не пройдено',calculated:'Рассчитано',stable:'Устойчиво',unstable:'Неустойчиво',accounted_in_loads:'Учтено в усилиях',not_evaluated:'Не выполнено',not_applicable:'Неприменимо',indeterminate:'Не определено'};
const reasons={confirm_eccentricity_in_source:'Укажите, включён ли случайный эксцентриситет в исходные усилия.',skipped_uls_fail:'Прочность не пройдена; CScore пропустил проверку трещин.',skipped_uls_indeterminate:'Прочность не определена; трещины не проверены.',spiral_equivalence_not_confirmed_or_incomplete:'Нужны параметры и подтверждение приведённого сечения и навивки.',confirm_anchorage_and_section_support_distance:'Нужны коэффициент анкеровки и расстояния до опор в обе стороны.',native_member_unstable:'CScore установил потерю устойчивости.',explicit_section_only_scope:'Выбрана проверка только сечения.',native_result_not_finite:'CScore не получил конечный результат.',SLS_loads_missing:'Не заданы усилия SLS.',second_order_confirmed_in_source_not_independently_checked:'Второй порядок включён в усилия по подтверждению пользователя.'};
function requested(r,p){const k=r.check_id;if(k==='anchorage'||k==='detailing')return false;if(k.startsWith('shear_'))return p.transverse.mode==='screen';if(k==='inclined_moment')return p.calculation_context.inclined_moment_requested;if(k.includes('member'))return p.calculation_context.second_order_mode!=='section_only'&&(k!=='sls_member_stability'||p.rows.some(r=>r.basis==='SLS'));if(k.startsWith('crack_'))return p.rows.some(r=>r.basis==='SLS');return true;}
function renderResults(result,p){
 const all=result.stations[0].report.checks,checks=all.filter(r=>requested(r,p));
 const failed=checks.some(r=>['failed','unstable'].includes(r.status)),gaps=checks.some(r=>['not_evaluated','indeterminate','not_applicable'].includes(r.status));
 $('resultBadge').textContent=failed?'Есть превышение':gaps?'Есть невыполненные проверки':'Запрошенные проверки выполнены';$('resultBadge').className=failed?'fail':gaps?'missing':'pass';
 const body=checks.map(r=>{
  const d=r.details||{},k=r.check_id;let applied=d.applied,capacity=d.allowable,unit=d.units==='kN*m'?' кН·м':d.units==='kN'?' кН':'';
  if(k.startsWith('crack_width')){applied=d.width_mm;capacity=k.endsWith('long')?p.sls_settings.acrc_ult_long_mm:p.sls_settings.acrc_ult_short_mm;unit=' мм';}
  if(k==='crack_formation'){applied=p.rows.find(x=>x.basis==='SLS')?.m;capacity=d.mcrc_knm;unit=' кН·м';}
  let status=statuses[r.status]||r.status;if(k==='crack_formation'&&r.status==='calculated')status=d.cracked?'Образуются':'Не образуются';
  const cls=['passed','stable'].includes(r.status)?'pass':['failed','unstable'].includes(r.status)?'fail':'missing';
  const demand=k==='normal_strength'||k==='normal_member_strength'?fmt(p.rows[0].n)+' / '+fmt(p.rows[0].m):Number.isFinite(applied)?fmt(applied)+unit:'—';
  const explanation=reasons[r.reason]||r.reason||'';
  return `<tr><td>${esc(names[k]||k)}<details><summary>Подробности CScore</summary>${explanation?'<p>'+esc(explanation)+'</p>':''}<pre>${esc(JSON.stringify(r,null,2))}</pre></details></td><td>${esc(demand)}</td><td>${Number.isFinite(capacity)?esc(fmt(capacity)+unit):'—'}</td><td>${fmt(r.utilization)}</td><td class="${cls}">${esc(status)}</td></tr>`;
 }).join('');
 $('results').innerHTML='<table><thead><tr><th>Проверка</th><th>Воздействие / раскрытие</th><th>Предел</th><th>Использ.</th><th>Результат</th></tr></thead><tbody>'+body+'</tbody></table><p class="resultnote">Коэффициенты использования получены от CScore. Для N/M указан исходный N / M; коэффициент относится к совместному действию. Для Q ядро возвращает силы на расчётную захватку (СВГ) или сваю. В таблице трещинообразования — M / Mcrc; итог определяется CScore с учётом N. Подробные результаты, единицы и η доступны в строке проверки.</p><p class="resultnote">Область проверки: '+(p.calculation_context.second_order_mode==='section_only'?'локальное сечение, без устойчивости элемента':'локальное сечение с заданным учётом второго порядка')+'. Длина анкеровки и деталировка не проверены.</p>';
}
function load(p){
 project=p;project.max_tier=1;project.zones=[];project.transverse_zones=[];
 $('kind').value=project.kind;const c=p.calculation_context||{},u=p.rows.find(r=>r.basis==='ULS'),s=p.rows.find(r=>r.basis==='SLS');
 for(const [id,value] of Object.entries({n:u?.n??'',m:u?.m??'',q:u?.q??'',slsN:s?.n??'',slsM:s?.m??'',slsLong:s?.m_long??'',limitLong:p.sls_settings.acrc_ult_long_mm,limitShort:p.sls_settings.acrc_ult_short_mm,secondOrder:c.second_order_mode||'section_only',l0x:c.l0_x_m??'',l0y:c.l0_y_m??'',psi:c.psi??1,supportDown:c.sections?.[0]?.support_distance_positive_m??'',supportUp:c.sections?.[0]?.support_distance_negative_m??'',anchorage:c.anchorage_factor??'',spiralA:c.circular_shear?.a_mm??''}))$(id).value=value;
 $('withSls').checked=!!s;$('withQ').checked=p.transverse.mode==='screen';$('withInclinedM').checked=!!c.inclined_moment_requested;
 $('confirmed').checked=!!p.materials_confirmed;$('eccentricity').checked=!!p.eccentricity_confirmed;$('tiesConfirmed').checked=!!p.transverse.constructive_confirmed;$('spiralConfirmed').checked=!!c.circular_shear?.confirmed;$('anchorageConfirmed').checked=!!c.anchorage_confirmed;
 $('materialSummary').textContent='Характеристики из исходного шаблона B30 / A500 или открытого файла. При изменении прочности класс автоматически не назначается.';
 renderGeometry();renderMaterials();invalidate();error();
}
$('sectionForm').addEventListener('input',e=>{
 if(!project||busy)return;const el=e.target;
 if(el.dataset.path){setPath(el.dataset.path,Number(el.value)*Number(el.dataset.factor));if(el.dataset.path.startsWith('materials.')||el.dataset.path.startsWith('sls_settings.'))$('confirmed').checked=false;else if(!el.dataset.path.startsWith('transverse.'))$('spiralConfirmed').checked=false;
  if(el.dataset.path.startsWith('transverse.')){project[project.kind].ties_diameter_mm=project.transverse.diameter_mm;$('tiesConfirmed').checked=false;$('spiralConfirmed').checked=false;}draw();}
 invalidate();error();
});
$('sectionForm').addEventListener('change',e=>{if(!project)return;if(e.target.id==='kind'){
 project.kind=$('kind').value;$('tiesConfirmed').checked=false;$('spiralConfirmed').checked=false;renderGeometry();
 }toggles();});
$('sectionForm').onsubmit=async e=>{
 e.preventDefault();if(busy)return;error();let p;try{p=build();}catch(ex){error(ex.message);return;}
 busy=true;const controls=[...$('sectionForm').querySelectorAll('input,select,button')],disabled=controls.map(e=>e.disabled);controls.forEach(e=>e.disabled=true);$('open').disabled=true;$('save').disabled=true;$('run').textContent='Расчёт…';
 try{invalidate();const start=await api('section-calculate',{project:p});while(true){await new Promise(r=>setTimeout(r,400));const job=await api('job?id='+encodeURIComponent(start.job));$('progress').textContent=job.message;if(job.status==='error')throw Error(job.message);if(job.status==='done'){lastResult=job.result;renderResults(lastResult,p);break;}}}
 catch(ex){error(ex.message);$('resultBadge').textContent='Расчёт не выполнен';}
 finally{busy=false;controls.forEach((e,i)=>e.disabled=disabled[i]);$('open').disabled=false;$('save').disabled=false;$('run').textContent='Рассчитать';$('progress').textContent='Заданное армирование · без подбора';}
};
$('save').onclick=()=>{try{const p=build(),a=document.createElement('a');a.href=URL.createObjectURL(new Blob([JSON.stringify(p,null,2)],{type:'application/json'}));a.download='section-'+p.kind+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}catch(ex){error(ex.message);}};
$('open').onclick=()=>$('file').click();$('file').onchange=async e=>{try{const p=JSON.parse(await e.target.files[0].text());if(p.version!==1||!['wall','pile'].includes(p.kind)||new Set(p.rows.map(r=>r.z)).size!==1||p.zones?.length||p.transverse_zones?.length)throw Error('Здесь открывается файл одного сечения с постоянным армированием. Проект с эпюрами откройте в разделе «Эпюры и подбор».');if(p.kind==='pile'&&p.force_scale!=='per_member'||p.moment_sign!==1)throw Error('Для ручного режима нужны усилия на один элемент и исходный знак M.');load(p);}catch(ex){error(ex.message);}finally{$('file').value='';}};
api('default').then(p=>{
 p.wall={...p.wall,panel_width_mm:1000,thickness_mm:600,cage_count:1,cage_width_mm:900,end_cover_mm:50,cover_mm:40,ties_diameter_mm:8};
 p.transverse={...p.transverse,mode:'screen',diameter_mm:8};p.pile.ties_diameter_mm=8;p.max_tier=1;
 p.rows=[{z:0,case:'ULS',basis:'ULS',n:-1000,m:150,q:100,m_long:null}];load(p);
}).catch(ex=>error(ex.message));
