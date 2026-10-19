'use strict';
const $=id=>document.getElementById(id),token=document.querySelector('meta[name=session]').content;
let project,result=null,stale=false,revision=0,selectedZ=0,phase='all',busy=false,catalog=null,profileAudit=null,profileResult=null,nativeResult=null;
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=(v,n=2)=>Number.isFinite(v)?v.toLocaleString('ru-RU',{maximumFractionDigits:n}):'—';
function message(s=''){ $('message').hidden=!s;$('message').textContent=s;document.querySelectorAll('.dialogerror').forEach(x=>x.remove());const d=document.querySelector('dialog[open]');if(s&&d){const p=document.createElement('p');p.className='dialogerror notice';p.textContent=s;d.prepend(p);} }
async function api(path,data){const r=await fetch('/api/'+path,{method:data===undefined?'GET':'POST',headers:{'X-Session':token,'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});const v=await r.json();if(!r.ok)throw Error(v.error||'Ошибка запроса');return v;}
// Diagnostic only: the closest M'≈±Q convention never modifies imported forces.
function refreshProfileAudit(){
 if(!project||!$('advancedQ').open)return;
 const rev=revision;
 api('profile-audit',project).then(value=>{
   if(rev===revision){profileAudit=value;renderInspection();}
 }).catch(error=>{
   if(rev===revision){profileAudit={error:error.message};renderInspection();}
 });
}
function dirty(){revision++;nativeResult=null;profileResult=null;profileAudit=null;refreshProfileAudit();stale=true;$('resultState').textContent=result?'Данные изменены · пересчитайте':'Расчёт не выполнен';renderCharts();renderInspection();}
const fieldsWall=[['panel_width_mm','Ширина захватки, мм'],['thickness_mm','Толщина стены, мм'],['cage_count','Число каркасов'],['cage_width_mm','Ширина каркаса, мм'],['end_cover_mm','Торцевой защитный слой, мм'],['cover_mm','Защитный слой до хомута, мм'],['ties_diameter_mm','Диаметр хомута, мм'],['min_clear_spacing_mm','Минимальный просвет, мм'],['min_cage_gap_mm','Промежуток каркасов, мм']];
const fieldsPile=[['diameter_mm','Диаметр сваи, мм'],['cover_mm','Защитный слой до навивки, мм'],['ties_diameter_mm','Диаметр стержня навивки, мм'],['min_clear_spacing_mm','Минимальный просвет, мм']];
function invalidateCircularEquivalence(){if(project.calculation_context?.circular_shear)project.calculation_context.circular_shear.confirmed=false;}
function inputField(label,value,attrs){return `<label>${esc(label)}<input type="number" value="${esc(value)}" ${attrs}></label>`;}
function renderForms(){
 $('title').value=project.title;$('kind').value=project.kind;$('force_scale').value=project.force_scale;$('spacing_m').value=project.spacing_m;
 $('moment_sign').value=project.moment_sign;$('max_tier').value=project.max_tier;
 $('materials_confirmed').checked=project.materials_confirmed;$('eccentricity_confirmed').checked=project.eccentricity_confirmed;
 const kind=project.kind,geom=project[kind];
 $('geometry').innerHTML=(kind==='wall'?fieldsWall:fieldsPile).map(([k,l])=>inputField(l,geom[k],`data-geom="${k}" min="0" step="1"`)).join('');
 $('reinforcement').innerHTML=kind==='wall'?['Грань y−','Грань y+'].map((f,i)=>inputField(`${f} · стержней фона на каркас`,project.background[i],`data-bg="${i}" min="2" step="1"`)+inputField(`${f} · диаметр, мм`,project.diameters[i],`data-dia="${i}" min="1"`)).join(''):inputField('Стержней фона в кольце',project.pile_background,'data-pile="pile_background" min="6" step="1"')+inputField('Диаметр стержня, мм',project.pile_diameter,'data-pile="pile_diameter" min="1"');
 $('spacingLabel').hidden=kind!=='pile'||project.force_scale!=='per_metre';
 document.querySelectorAll('[data-geom]').forEach(e=>e.onchange=()=>{project[kind][e.dataset.geom]=Number(e.value);if(kind==='wall')invalidateTieGeometry();else invalidateCircularEquivalence();dirty();});
 document.querySelectorAll('[data-bg]').forEach(e=>e.onchange=()=>{project.background[+e.dataset.bg]=Number(e.value);dirty();renderZones();});
 document.querySelectorAll('[data-dia]').forEach(e=>e.onchange=()=>{project.diameters[+e.dataset.dia]=Number(e.value);dirty();});
 document.querySelectorAll('[data-pile]').forEach(e=>e.onchange=()=>{project[e.dataset.pile]=Number(e.value);invalidateCircularEquivalence();dirty();renderZones();});
 // Old saved projects have no transverse block: safely default to no Q verdict.
 const t=project.transverse||(project.transverse={mode:'off',diameter_mm:10,spacing_mm:200,effective_legs_per_cage:2,rsw_kpa:170000,constructive_confirmed:false});
 $('shearSection').hidden=false;
 $('screenQ').checked=t.mode==='screen';
 $('qFields').hidden=t.mode!=='screen';
 $('qLegs').closest('label').hidden=kind==='pile';
 $('qConstructive').closest('label').lastChild.textContent=kind==='pile'?' Требования к навивке и её анкеровке подтверждены':' Требования 10.3 подтверждены; рабочие ветви обоснованы';
 $('wallQLayout').hidden=kind!=='wall';$('realTieDetails').hidden=kind!=='wall';
 for(const [id,key] of [['qDiameter','diameter_mm'],['qSpacing','spacing_mm'],['qLegs','effective_legs_per_cage'],['qRsw','rsw_kpa']])$(id).value=t[key];
 $('qConstructive').checked=!!t.constructive_confirmed;
 const choices=project.q_allowed_steps_mm||[100,150,200,300];
 $('qPitchOptions').value=choices.join('; ');
 $('qPitchChoices').innerHTML=choices.map(n=>'<option value="'+esc(n)+'"></option>').join('');
 $('exportSchedule').hidden=kind!=='wall';
 $('exportDrawing').hidden=kind!=='wall';
 const po=project.profile_options||(project.profile_options={orientation:'same',axes_confirmed:false});
 $('profileOrientation').value=po.orientation;
 $('profileAxesConfirmed').checked=po.axes_confirmed===true;
 $('profileFields').hidden=kind!=='wall';
 $('profileCheck').disabled=t.mode!=='screen';
 renderQZones();
 const ties=project.transverse_layout||(project.transverse_layout={mode:'proposed',positions_mm:[],geometry_confirmed:false});
 $('tieMode').value=ties.mode;
 $('tiePositionsFields').hidden=ties.mode!=='specified';
 $('tiePositions').value=(ties.positions_mm||[]).join('; ');
 $('tieGeometryConfirmed').checked=ties.geometry_confirmed===true;
 $('sourceLabel').textContent=project.source?.type==='plaxis_output'?'PLAXIS Output':project.source?.note?'Пример · ручной ввод':'Ручной ввод';
 $('table').textContent=`Таблица усилий · ${project.rows.length}`;
 const phases=[...new Set(project.rows.map(r=>r.basis+' · '+r.case))];
 if(!phases.includes(phase))phase='all';
 $('phase').innerHTML='<option value="all">Все сочетания</option>'+phases.map(p=>`<option value="${esc(p)}">${esc(p)}</option>`).join('');$('phase').value=phase;
}
function rows(){const factor=project.kind==='pile'&&project.force_scale==='per_metre'?project.spacing_m:1;return project.rows.map(r=>({...r,n:r.n*factor,m:r.m*factor*project.moment_sign,q:r.q*factor,m_long:r.m_long==null?null:r.m_long*factor*project.moment_sign}));}
function zs(){return [...new Set(project.rows.map(r=>+r.z))].sort((a,b)=>b-a);}
function tierAt(z){const active=project.zones.filter(v=>z<=v.top&&z>=v.bottom);return [0,1].map(i=>Math.max(1,...active.map(a=>a.tiers[i])));}
function countAt(t,i){return project.kind==='wall'?(project.background[i]-1)*t+1:project.pile_background*t;}
function area(t,i){let count=countAt(t,i),d=project.kind==='wall'?project.diameters[i]:project.pile_diameter;return count*Math.PI*d*d/400*(project.kind==='wall'?project.wall.cage_count/(project.wall.panel_width_mm/1000):1);}
function renderCharts(){if(!project)return;const points=zs();if(points.length<2){$('charts').innerHTML='<p class="empty">Нужно минимум две отметки</p>';return;}
 const top=points[0],bottom=points.at(-1),height=460,Y=z=>55+(top-z)/(top-bottom)*350;
 const r=rows().filter(r=>phase==='all'||r.basis+' · '+r.case===phase),unit=project.kind==='wall'?'/м':'';
 const plots=[['n','N',`кН${unit}`],['m','M',`кН·м${unit}`],...($('showQ').checked?[['q','Q',`кН${unit}`]]:[]),['as0',project.kind==='wall'?'As · y−':'As · кольцо',`см²${unit}`],...(project.kind==='wall'?[['as1','As · y+','см²/м']]:[])];
 $('charts').innerHTML=plots.map(([key,name,units],idx)=>{
  const isAs=key.startsWith('as'),face=+key.at(-1);let series=[];
  if(!isAs){const groups=new Map();for(const row of r){const k=row.basis+' · '+row.case;if(!groups.has(k))groups.set(k,[]);groups.get(k).push([row[key],row.z]);}series=[...groups.values()].map(a=>a.sort((a,b)=>b[1]-a[1]));if(phase==='all'&&groups.size>1){for(const fn of [Math.min,Math.max])series.push(points.map(z=>[fn(...r.filter(v=>v.z===z).map(v=>v[key])),z]));}}
  else{const levels=new Set([...points,...project.zones.flatMap(z=>[z.top,z.bottom])]);const zlist=[...levels].sort((a,b)=>b-a);let adopted=[];
   for(let j=0;j<zlist.length-1;j++){let a=zlist[j],b=zlist[j+1],t=tierAt((a+b)/2)[face],v=area(t,face);adopted.push([v,a],[v,b]);}
   series.push(adopted);if(result&&!stale)series.push(result.stations.filter(s=>s.required_areas).map(s=>[s.required_areas[face],s.z]));
  }
  const vals=series.flat().map(p=>p[0]).filter(Number.isFinite);let lo=isAs?0:Math.min(0,...vals),hi=Math.max(isAs?area(1,face):1,...vals);
  if(hi===lo)hi=lo+1;const pad=(hi-lo)*.1;if(!isAs){lo-=pad;hi+=pad;}else hi+=pad;
  const X=v=>26+(v-lo)/(hi-lo)*130;let svg=`<svg viewBox="0 0 185 ${height}" role="img" aria-label="Эпюра ${esc(name)}" data-chart="${key}"><text x="90" y="18" text-anchor="middle" font-size="13" fill="#263442" font-weight="600">${esc(name)}</text><text x="90" y="34" text-anchor="middle" font-size="10" fill="#798690">${esc(units)}</text>`;
  for(const z of points)svg+=`<line x1="25" y1="${Y(z)}" x2="160" y2="${Y(z)}" stroke="#e7edf2"/><text x="20" y="${Y(z)+3}" text-anchor="end" fill="#7c8b97" font-size="9">${idx===0?fmt(z):''}</text>`;
  svg+=`<line x1="${X(0)}" y1="50" x2="${X(0)}" y2="405" stroke="#b9c5ce"/>`;
  for(let j=0;j<series.length;j++){const a=series[j].filter(v=>v.every(Number.isFinite));if(!a.length)continue;const colour=isAs?(j===0?'#245e8c':'#bd8333'):['#6688a2','#8aa6b9','#b3bfc9'][j%3];svg+=`<polyline points="${a.map(([v,z])=>X(v)+','+Y(z)).join(' ')}" fill="none" stroke="${colour}" stroke-width="${isAs?2.3:1.6}" ${isAs&&j===1?'stroke-dasharray="4 3"':''}/>`;}
  if(isAs){const bg=X(area(1,face));svg+=`<line x1="${bg}" x2="${bg}" y1="55" y2="405" stroke="#6688a2" stroke-dasharray="3 4"/>`;for(let j=0;j<project.zones.length;j++){const zone=project.zones[j];for(const edge of ['top','bottom'])svg+=`<circle data-boundary="${j}:${edge}" cx="163" cy="${Y(zone[edge])}" r="4" fill="#245e8c" style="cursor:ns-resize"><title>Перетащить границу участка</title></circle>`;}if(!result||stale)svg+='<text x="90" y="435" text-anchor="middle" font-size="9" fill="#88949e">подбор не подтверждён</text>';}
  if(selectedZ<=top&&selectedZ>=bottom)svg+=`<line x1="23" y1="${Y(selectedZ)}" x2="163" y2="${Y(selectedZ)}" stroke="#245e8c" stroke-dasharray="2 3"/><circle cx="23" cy="${Y(selectedZ)}" r="2.5" fill="#245e8c"/>`;
  svg+=`<text x="26" y="423" font-size="9" fill="#798690">${fmt(lo,1)}</text><text x="156" y="423" text-anchor="end" font-size="9" fill="#798690">${fmt(hi,1)}</text></svg>`;return svg;
 }).join('');
 document.querySelectorAll('[data-boundary]').forEach(handle=>handle.onpointerdown=e=>{e.stopPropagation();e.preventDefault();const svg=handle.closest('svg'),rect=svg.getBoundingClientRect(),[index,edge]=handle.dataset.boundary.split(':');const move=ev=>{handle.setAttribute('cy',Math.max(55,Math.min(405,(ev.clientY-rect.top)/rect.height*height)));};const up=ev=>{window.removeEventListener('pointermove',move);const y=Math.max(55,Math.min(405,(ev.clientY-rect.top)/rect.height*height));let z=Math.round((top-(y-55)/350*(top-bottom))*10)/10;const zone=project.zones[+index];z=edge==='top'?Math.max(zone.bottom+1e-6,Math.min(top,z)):Math.min(zone.top-1e-6,Math.max(bottom,z));zone[edge]=z;dirty();renderZones();};window.addEventListener('pointermove',move);window.addEventListener('pointerup',up,{once:true});});
 document.querySelectorAll('[data-chart]').forEach(s=>s.onclick=e=>{const rect=s.getBoundingClientRect(),yp=(e.clientY-rect.top)/rect.height*height;const z=top-(yp-55)/350*(top-bottom);selectedZ=points.reduce((a,b)=>Math.abs(b-z)<Math.abs(a-z)?b:a);renderCharts();renderInspection();});
}
function metric(a,b){return `<div class="metric"><span>${esc(a)}</span><b>${esc(b)}</b></div>`;}

function nativeContext(){return {second_order_mode:'unspecified',l0_x_m:null,l0_y_m:null,psi:1,
 inclined_moment_requested:false,anchorage_confirmed:false,anchorage_factor:null,sections:[],...(project.calculation_context||{})};}
function renderNativeChecks(){
 const station=nativeResult?.stations.find(s=>s.z===selectedZ);
 if(!station)return '';
 const names={normal_strength:'Прочность N/M',crack_formation:'Образование трещин',crack_width_long:'Продолжительное раскрытие',crack_width_short:'Непродолжительное раскрытие',shear_strip:'Бетонная полоса · Q',shear_inclined:'Наклонное сечение · Q',inclined_moment:'Наклонное сечение · M',member_stability:'Второй порядок · ULS',sls_member_stability:'Второй порядок · SLS',normal_member_strength:'Прочность с учётом второго порядка',anchorage:'Длина анкеровки',detailing:'Конструктивные требования'};
 const statuses={passed:'Пройдено',failed:'Не пройдено',calculated:'Рассчитано',not_evaluated:'Не выполнено',not_applicable:'Неприменимо',indeterminate:'Неопределённо',accounted_in_loads:'Учтено в исходных усилиях',stable:'Проверка η выполнена',unstable:'Потеря устойчивости по CScore'};
 const reasons={spiral_equivalence_not_confirmed_or_incomplete:'Заполните и подтвердите геометрию приведения; для учёта навивки нужны её параметры и конструктивное подтверждение',circular_biaxial_moment_not_supported:'Для этой приведённой модели косой изгиб не поддерживается',SLS_loads_missing:'Не заданы нормативные усилия SLS',second_order_context_missing:'Не задан учёт второго порядка',explicit_section_only_scope:'Выбрана проверка только сечения',confirm_eccentricity_in_source:'Подтвердите эксцентриситет в исходных усилиях',second_order_confirmed_in_source_not_independently_checked:'Учёт в исходных усилиях подтверждён инженером; отдельно не проверялся',transverse_geometry_or_Q_missing:'Не заданы данные поперечной арматуры или Q',inclined_moment_not_requested:'Проверка не запрошена',confirm_anchorage_and_section_support_distance:'Нужны коэффициент анкеровки и положительные расстояния до опор в обе стороны',anchorage_factor_is_input_not_an_anchorage_length_check:'Длина анкеровки будет проверяться на этапе армирования',reinforcement_detailing_stage_deferred:'Этап конструктивного оформления отложен',native_member_unstable:'CScore установил потерю устойчивости',native_SLS_member_unstable:'CScore установил потерю устойчивости при нормативных усилиях SLS',member_strength_not_confirmed:'Прочность с учётом второго порядка не подтверждена',member_eta_requires_consistent_N_M_Q_profile:'После η нужен согласованный профиль N/M/Q; локальное сочетание не подменяется',skipped_uls_fail:'Пропущено: прочность N/M не пройдена',skipped_uls_indeterminate:'Пропущено: прочность N/M не определена',native_result_not_finite:'Ядро не вернуло конечный коэффициент'};
 let html='<h3>Проверки CScore · принятое сечение</h3>';
 html+='<p class="hint">Проверены заданные сечения. Подбор армирования не изменён. Полный допуск для КЖ не выдан.</p>';
 for(const r of station.report.checks){
  const title=(names[r.check_id]||r.check_id)+(r.case_id?' · '+r.case_id:'')
   +(r.details?.equivalence?.concrete_only?' · без вклада навивки':'');
  html+=metric(title,(statuses[r.status]||r.status)+(Number.isFinite(r.utilization)?' · η='+fmt(r.utilization,3):''));
  if(r.check_id==='crack_formation'&&r.status==='calculated')html+='<p class="hint">'+(r.details?.cracked?'Трещины образуются':'По CScore трещины не образуются')+'</p>';
  if(r.check_id.startsWith('crack_width')&&Number.isFinite(r.details?.width_mm))html+='<p class="hint">Раскрытие: '+fmt(r.details.width_mm,3)+' мм</p>';
  if(r.details?.directions_complete===false)html+='<p class="hint">Проверены не все направления. Известное превышение сохранено.</p>';
  if(r.reason)html+='<p class="hint">'+esc(reasons[r.reason]||r.reason)+'</p>';
  if(r.basis==='research_constant_profile')html+='<p class="hint">Локальная оценка по постоянным усилиям, не проверка конструкции целиком.</p>';
  if(r.details?.equivalence){const e=r.details.equivalence;
   html+='<p class="hint">'+(e.concrete_only?'Бетонная часть, без вклада навивки':'По принятой приведённой модели спирали')+': b = '+fmt(e.b_m*1000,1)+' мм; h₀ = '+fmt(e.h0_m*1000,1)+' мм; Aₛw,экв = '+fmt(e.effective_asw_mm2,1)+' мм².';
   if(e.concrete_only)html+=' Если бетонной части недостаточно, нужен обоснованный расчёт вклада навивки. Геометрия приведения принята инженером.';
   else if(e.two_conditional_legs)html+=' Один виток — две условные ветви; шаг равен полному шагу навивки. Принятое инженерное допущение для приведённого сечения.';
   else html+=' χ = '+fmt(e.circular_efficiency,3)+'; проекция витка = '+fmt(e.helix_projection,4)+'. Сверка модели с АРБАТ не выполнена.';
   html+=' Обоснование: '+esc(e.efficiency_basis)+'.</p>';}
 }
 return html;
}
async function calculateConnection(){
 if(busy)return;message();const rev=revision;nativeResult=null;renderInspection();busy=true;document.body.classList.add('busy');
 try{const start=await api('connection-calculate',{project});
  while(true){await new Promise(r=>setTimeout(r,600));const job=await api('job?id='+encodeURIComponent(start.job));$('progress').textContent=job.message;
   if(job.status==='error')throw Error(job.message);
   if(job.status==='done'){if(revision!==rev){message('Данные изменились во время проверки CScore. Повторите проверку.');break;}
    nativeResult=job.result;renderInspection();message(nativeResult.status==='failed'?'CScore: есть непрошедшие проверки.':nativeResult.status==='incomplete'?'CScore: есть невыполненные или неприменимые проверки.':'Расчёт выполнен. Допуск для выпуска КЖ не выдан.');break;}}
 }catch(e){message(e.message);}finally{busy=false;document.body.classList.remove('busy');$('progress').textContent='Локальный расчёт · данные остаются на компьютере';}
}
function renderInspection(){if(!project)return;$('elevation').textContent=fmt(selectedZ)+' м';const t=tierAt(selectedZ);let html='<h3>Принятое армирование</h3>';
 for(let i=0;i<(project.kind==='wall'?2:1);i++){const bg=countAt(1,i),total=countAt(t[i],i),d=project.kind==='wall'?project.diameters[i]:project.pile_diameter;html+=metric(project.kind==='wall'?`Грань y${i?'+':'−'}`:'Кольцо',`${bg} + ${total-bg} Ø${d}`)+metric('As'+(project.kind==='wall'?'/м':''),fmt(area(t[i],i))+' см²');}
 const station=result&&!stale?result.stations.find(s=>s.z===selectedZ):null;
 if(station){const c=station.adopted;html+=`<div class="status ${station.checked?'':'bad'}">${station.checked?'N/M предварительно пройдены; Q отдельно':'N/M: требуется проверка'}</div>`;
 if(c){html+=metric('Прочность',fmt(c.max_utilization,3));const governing=c.loads?.filter(l=>Number.isFinite(l.utilization)).sort((a,b)=>b.utilization-a.utilization)[0];if(governing)html+=metric('Определяет',governing.id);
 html+=metric('Трещины',({'preliminary_below_limit':'предварительно проходят','preliminary_exceeds_limit':'превышение','engineering_review':'пограничный результат','not_evaluated_uls_fail':'пропущены: отказ ULS','not_evaluated_uls_indeterminate':'пропущены: ULS неизвестен','research_indeterminate':'неопределённо'})[c.sls_status]||'не запрошены');}
 else html+='<p class="hint">Нет геометрически допустимого варианта.</p>';}
 else html+=`<div class="status">${stale&&result?'Результат устарел. Перепроверьте принятое.':'Расчёт сечения не выполнен'}</div>`;
 html+=renderNativeChecks();
 html+='<h3>Совместные усилия</h3>';for(const r of rows().filter(r=>r.z===selectedZ&&(phase==='all'||r.basis+' · '+r.case===phase)))html+=`<p class="small">${esc(r.case)} · ${esc(r.basis)}</p>`+metric('N / M',fmt(r.n)+' / '+fmt(r.m));
 if(project.kind==='wall'){
  html+='<h3>Поперечное армирование</h3>';
  const q=project.transverse||{};
  html+=metric('Хомут Ø / основной шаг',fmt(q.diameter_mm,0)+' / '+fmt(q.spacing_mm,0)+' мм');
  html+='<p class="hint">Проверка Q для выпуска КЖ ещё не подтверждена.</p>';
  html+='<details class="q-inspection-advanced"><summary>Расчёт Q и замечания</summary>';
  if(profileResult?.profile_results){
    html+='<h3>Эпюры Q · фоновое армирование</h3>';
    const l=profileResult.stirrup_layout||profileResult.proposed_stirrup_layout;
    if(l){
      const specified=l.assumption==='user_supplied_absolute_tie_coordinates';
      html+=metric(specified?'Хомутов по заданным координатам':'Хомутов в условной раскладке',
                   fmt(l.link_count_per_cage,0)+' на каркас');
      html+=metric('Максимальный промежуток',fmt(l.maximum_center_to_center_gap_mm)+' мм');
      html+='<p class="hint">'+(specified
       ? (l.actual_layout_confirmed?'Координаты подтверждены инженером. ':'Координаты ещё не подтверждены. ')+'Несущая способность по Q, анкеровка и работа ветвей не подтверждены.'
       : 'Первый хомут условно поставлен в начале каждого участка. Фактическое положение и анкеровка не подтверждены.')+'</p>';
    }
    if(profileResult.uniform_pitch_mm!=null){
      html+=metric('Постоянный шаг в CScore',fmt(profileResult.uniform_pitch_mm)+' мм');
    }
    html+='<p class="hint">Исследовательский расчёт CScore по профилю без опор и усилений. Не нормативный допуск.</p>';
    for(const r of profileResult.profile_results){
      const meaning=({'research_profile_below_one':'расчётный η ≤ 1','research_profile_exceeds_one':'расчётный η > 1','indeterminate':'не определено'})[r.status]||'не определено';
      html+='<p class="small">'+esc(r.id)+' · '+esc(meaning)+'</p>';
      html+=metric('η по эпюре',fmt(r.utilization,3));
      if(r.governing_formula)html+=metric('Условие',r.governing_formula);
      const linkCount=profileResult.critical_projection_links?.find(x=>x.id===r.id);
      if(['proposed_layout_count_only','specified_layout_count_only'].includes(linkCount?.status)){
        html+=metric('Хомутов на критической проекции',fmt(linkCount.ties_crossing_per_cage,0)+' на каркас');
      }else if(linkCount?.status==='outside_sampled_profile'){
        html+='<p class="hint">Критическая проекция Q выходит за пределы заданной эпюры: количество хомутов не определено.</p>';
      }else if(linkCount){
        html+='<p class="hint">Расположение хомутов на критической проекции не определено.</p>';
      }
      if(r.governing_s_m!=null)html+=metric('Стоянка от верха',fmt(r.governing_s_m)+' м');
      if(Array.isArray(r.warnings)&&r.warnings.length){
        html+='<p class="hint">Замечания CScore:</p>';
        for(const w of r.warnings.slice(0,5))html+='<p class="hint">· '+esc(w)+'</p>';
        if(r.warnings.length>5||r.warnings_truncated)
          html+='<p class="hint">Показаны не все замечания CScore.</p>';
      }
      if(r.error)html+='<p class="hint">'+esc(r.error)+'</p>';
    }
  }
  if(profileAudit?.error){
    html+='<p class="hint">Диагностика M/Q недоступна: '+esc(profileAudit.error)+'</p>';
  }else if(profileAudit?.profiles){
    const profiles=profileAudit.profiles.filter(r=>r.basis==='ULS');
    const problematic=profiles.filter(r=>r.status!=='compatible');
    const base=profiles.length?profiles.length+' сочетаний; '+problematic.length+' требуют проверки':'нет расчётных ULS эпюр';
    html+=metric('Согласованность M–Q',base);
    for(const p of problematic.slice(0,4)){
      const label=({'inconsistent':'M и Q не согласованы','duplicate_station':'повторные отметки','sign_ambiguous':'знак Q неоднозначен','zero_signal':'нет изменений M/Q','insufficient':'недостаточно точек'})[p.status]||p.status;
      html+='<p class="hint">'+esc(p.case)+': '+label+'</p>';
    }
    if(!problematic.length&&profiles.length){
      const signs=[...new Set(profiles.map(p=>p.closest_convention))];
      html+='<p class="hint">По производной M ближе знак '+esc(signs.join(', '))+'. Знак не меняется автоматически.</p>';
    }
    html+='<p class="hint">Диагностика эпюр не подтверждает несущую способность.</p>';
  }
  if(!station?.shear_screening)html+='<div class="status">Q не проверена. Включите оценку и пересчитайте.</div>';
  else{
   const shear=station.shear_screening;
   for(const r of shear){
    const verdict=({'research_screen_pass':'условно проходит','research_screen_fail':'превышение','indeterminate':'не определено'})[r.status]||'неизвестно';
    html+='<p class="small">'+esc(r.id)+' · исследовательская оценка</p>';
    html+=metric('Q / η',fmt(r.q_kn)+' кН / '+fmt(r.utilization,3));
    html+=metric('Результат',verdict);
    if(r.governing_formula)html+=metric('Условие СП',r.governing_formula);
    if(r.error)html+='<p class="hint">'+esc(r.error)+'</p>';
   }
   html+='<p class="hint">Без профиля усилий, опор и анкеровки нормативного допуска нет.</p>';
  }
  html+='</details>';
 }
 $('inspection').innerHTML=html;
}
function tierOptions(i,t){return [1,2,3].map(x=>`<option value="${x}" ${x===t?'selected':''}>${countAt(x,i)} ст.</option>`).join('');}
function invalidateTieGeometry(){
 if(project.transverse_layout)project.transverse_layout.geometry_confirmed=false;
 if($('tieGeometryConfirmed'))$('tieGeometryConfirmed').checked=false;
}
function renderQZones(){
 const host=$('qZoneTable');if(!host)return;
 const zones=project.transverse_zones||(project.transverse_zones=[]);
 host.innerHTML=zones.length?zones.map((z,i)=>`<div class="q-zone">
  <label>Верх, м<input aria-label="Q верх ${i+1}" data-q-zone="${i}" data-q-key="top" type="number" step="0.1" value="${esc(z.top)}"></label>
  <label>Низ, м<input aria-label="Q низ ${i+1}" data-q-zone="${i}" data-q-key="bottom" type="number" step="0.1" value="${esc(z.bottom)}"></label>
  <label>Шаг, мм<input aria-label="Q шаг ${i+1}" data-q-zone="${i}" data-q-key="spacing_mm" type="number" min="1" step="5" list="qPitchChoices" value="${esc(z.spacing_mm)}"></label>
  <button type="button" data-q-remove="${i}" title="Удалить участок Q">×</button>
 </div>`).join(''):'<p class="hint">Участков нет — основной шаг по всей длине.</p>';
 host.querySelectorAll('[data-q-zone]').forEach(el=>el.onchange=()=>{
   project.transverse_zones[+el.dataset.qZone][el.dataset.qKey]=Number(el.value);
   project.transverse.constructive_confirmed=false;$('qConstructive').checked=false;
   invalidateTieGeometry();dirty();
 });
 host.querySelectorAll('[data-q-remove]').forEach(el=>el.onclick=()=>{
   project.transverse_zones.splice(+el.dataset.qRemove,1);
   project.transverse.constructive_confirmed=false;$('qConstructive').checked=false;
   invalidateTieGeometry();dirty();renderQZones();
 });
}
function renderZones(){if(!project.zones.length){$('zoneTable').innerHTML='<p class="empty">Усилений нет. Выполните подбор или добавьте участок вручную.</p>';return;}
 $('zoneTable').innerHTML='<table><thead><tr><th>Верх, м</th><th>Низ, м</th><th>Всего '+(project.kind==='wall'?'y−':'кольцо')+'</th>'+(project.kind==='wall'?'<th>Всего y+</th>':'')+'<th></th></tr></thead><tbody>'+project.zones.map((z,j)=>`<tr><td><input aria-label="Верх участка ${j+1}" type="number" step="0.1" value="${z.top}" data-zone="${j}" data-key="top"></td><td><input aria-label="Низ участка ${j+1}" type="number" step="0.1" value="${z.bottom}" data-zone="${j}" data-key="bottom"></td><td><select aria-label="Армирование первой грани" data-zone="${j}" data-tier="0">${tierOptions(0,z.tiers[0])}</select></td>${project.kind==='wall'?`<td><select aria-label="Армирование второй грани" data-zone="${j}" data-tier="1">${tierOptions(1,z.tiers[1])}</select></td>`:''}<td><button data-delete="${j}" title="Удалить участок">×</button></td></tr>`).join('')+'</tbody></table>';
 document.querySelectorAll('[data-zone]').forEach(e=>e.onchange=()=>{let z=project.zones[+e.dataset.zone];if(e.dataset.key)z[e.dataset.key]=+e.value;else z.tiers[+e.dataset.tier]=+e.value;dirty();});
 document.querySelectorAll('[data-delete]').forEach(e=>e.onclick=()=>{project.zones.splice(+e.dataset.delete,1);dirty();renderZones();});
}
function render(){renderForms();renderCharts();renderZones();renderInspection();}
async function calculate(recheck){if(busy)return;message();const rev=revision;busy=true;document.body.classList.add('busy');
 try{const start=await api('calculate',{project,recheck});while(true){await new Promise(r=>setTimeout(r,600));const job=await api('job?id='+encodeURIComponent(start.job));$('progress').textContent=job.message;if(job.status==='error')throw Error(job.message);if(job.status==='done'){if(revision!==rev){message('Во время расчёта данные изменились. Результат не применён; запустите расчёт заново.');break;}nativeResult=null;result=job.result;project.zones=result.zones;stale=false;$('resultState').textContent=result.all_checked?'N/M предварительно пройдены · Q не является допуском':'Есть непрошедшие нормальные сечения';renderZones();renderCharts();renderInspection();break;}}}
 catch(e){message(e.message);}finally{busy=false;document.body.classList.remove('busy');$('progress').textContent='Локальный расчёт · данные остаются на компьютере';}}
async function calculateProfile(){
 if(busy)return;
 message();
 const rev=revision;
 busy=true;document.body.classList.add('busy');
 try{
  if(project.kind!=='wall'||project.transverse?.mode!=='screen')
   throw Error('Сначала включите оценку Q для стены в грунте');
  if(project.profile_options?.axes_confirmed!==true)
   throw Error('Сначала подтвердите направление осей и знак M/Q');
  const start=await api('profile-calculate',{project});
  while(true){
   await new Promise(r=>setTimeout(r,600));
   const job=await api('job?id='+encodeURIComponent(start.job));
   $('progress').textContent=job.message;
   if(job.status==='error')throw Error(job.message);
   if(job.status==='done'){
    if(revision!==rev){message('Исходные данные изменились во время расчёта. Повторите проверку эпюр.');break;}
    profileResult=job.result;
    renderInspection();
    break;
   }
  }
 }catch(e){message(e.message);}
 finally{busy=false;document.body.classList.remove('busy');$('progress').textContent='Локальный расчёт · данные остаются на компьютере';}
}
function save(){const allowed=['version','title','kind','force_scale','spacing_m','moment_sign','materials_confirmed','eccentricity_confirmed','background','pile_background','diameters','pile_diameter','max_tier','wall','pile','materials','sls_settings','rows','zones','source','transverse','transverse_zones','transverse_layout','profile_options','q_allowed_steps_mm','calculation_context'];const clean=Object.fromEntries(allowed.map(k=>[k,project[k]]));const blob=new Blob([JSON.stringify(clean,null,2)],{type:'application/json'}),a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=(project.title||'Расчёт').replace(/[<>:"/\\|?*]/g,'_')+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}
$('screenQ').onchange=e=>{project.transverse.mode=e.target.checked?'screen':'off';dirty();renderForms();};
for(const [id,key] of [['qDiameter','diameter_mm'],['qSpacing','spacing_mm'],['qLegs','effective_legs_per_cage'],['qRsw','rsw_kpa']])$(id).onchange=e=>{
 project.transverse[key]=Number(e.target.value);
 if(project.kind==='pile')invalidateCircularEquivalence();
 project.transverse.constructive_confirmed=false;if(key==='spacing_mm')invalidateTieGeometry();dirty();renderForms();
};
$('qConstructive').onchange=e=>{project.transverse.constructive_confirmed=e.target.checked;dirty();};
$('profileOrientation').onchange=e=>{project.profile_options.orientation=e.target.value;
 project.profile_options.axes_confirmed=false;
 $('profileAxesConfirmed').checked=false;revision++;profileResult=null;renderInspection();};
$('profileAxesConfirmed').onchange=e=>{project.profile_options.axes_confirmed=e.target.checked;
 revision++;profileResult=null;renderInspection();};
$('addQZone').onclick=()=>{
 const levels=zs(),top=levels[0],bottom=levels.at(-1);
 const sorted=[...(project.transverse_zones||[])].sort((a,b)=>b.top-a.top);
 let cursor=top,gap=null;
 for(const z of sorted){
  if(z.top<cursor-1e-8){gap={top:cursor,bottom:z.top};break;}
  cursor=Math.min(cursor,z.bottom);
 }
 if(!gap&&cursor>bottom+1e-8)gap={top:cursor,bottom};
 if(!gap){message('Сначала освободите часть длины, изменив границы существующих участков.');return;}
 project.transverse_zones.push({...gap,spacing_mm:150});
 project.transverse.constructive_confirmed=false;$('qConstructive').checked=false;
 invalidateTieGeometry();dirty();renderQZones();message();
};
$('tieMode').onchange=e=>{
 const ties=project.transverse_layout||(project.transverse_layout={mode:'proposed',positions_mm:[],geometry_confirmed:false});
 ties.mode=e.target.value;ties.geometry_confirmed=false;dirty();renderForms();
};
$('tiePositions').onchange=e=>{
 const ties=project.transverse_layout;
 const raw=e.target.value.trim();
 const values=raw?raw.split(/[;\\s]+/).map(x=>Number(x.replace(',','.'))):[];
 if(values.some(x=>!Number.isFinite(x))){message('Некорректная отметка хомута. Используйте мм через точку с запятой.');return;}
 ties.positions_mm=values;ties.geometry_confirmed=false;
 $('tieGeometryConfirmed').checked=false;dirty();
};
$('tieGeometryConfirmed').onchange=e=>{
 project.transverse_layout.geometry_confirmed=e.target.checked;dirty();
};
$('qPitchOptions').onchange=e=>{
 const text=e.target.value.trim();
 const raw=text.split(/[;\\s]+/).filter(Boolean);
 const values=raw.map(s=>Number(s.replace(',','.')));
 if(!values.length||values.length>15||values.some(v=>!Number.isFinite(v)||v<=0||v>2000)
     ||new Set(values).size!==values.length){
   message('Введите от 1 до 15 различных положительных шагов, мм, разделив их точкой с запятой.');
   e.target.value=(project.q_allowed_steps_mm||[100,150,200,300]).join('; ');return;
 }
 project.q_allowed_steps_mm=values.sort((a,b)=>a-b);dirty();renderForms();message();
};
$('exportDrawing').onclick=async()=>{
 try{
  if(project.kind!=='wall')throw Error('Развёртка доступна для стены в грунте');
  const {status,dxf}=await api('reinforcement-drawing',project);
  if(status!=='DRAFT_NOT_VERIFIED'||!dxf.includes('AC1015'))throw Error('Не удалось получить черновик DXF');
  const blob=new Blob([dxf],{type:'application/dxf'}),a=document.createElement('a');
  a.href=URL.createObjectURL(blob);
  a.download=(project.title||'Стена').replace(/[<>:"/\\|?*]/g,'_')+'_развертка_черновик.dxf';
  a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);
  message('Экспортирована черновая развёртка. Не является рабочим листом КЖ.');
 }catch(e){message(e.message);}
};
$('exportSchedule').onclick=async()=>{
 try{
  if(project.kind!=='wall')throw Error('Ведомость участков доступна для стены в грунте');
  const {draft,csv}=await api('reinforcement-schedule',project);
  if(draft.status!=='DRAFT_NOT_VERIFIED')throw Error('Некорректный статус ведомости');
  const blob=new Blob([csv],{type:'text/csv;charset=utf-8'}),a=document.createElement('a');
  a.href=URL.createObjectURL(blob);
  a.download=(project.title||'Стена').replace(/[<>:"/\\|?*]/g,'_')+'_участки_черновик.csv';
  a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);
  message('Сохранён черновик ведомости участков. Для выпуска КЖ расчёты и деталировка требуют проверки.');
 }catch(e){message(e.message);}
};
$('advancedQ').ontoggle=()=>{if($('advancedQ').open)refreshProfileAudit();};
$('profileCheck').onclick=calculateProfile;
$('save').onclick=save;$('open').onclick=()=>$('file').click();$('file').onchange=async e=>{try{const p=JSON.parse(await e.target.files[0].text());const migrated=p.sls_settings?.preliminary_method==='arbat_conservative';if(migrated)p.sls_settings.preliminary_method='native_refined';await api('preview',p);project=p;project.zones=Array.isArray(p.zones)?p.zones:[];project.transverse_zones=Array.isArray(p.transverse_zones)?p.transverse_zones:[];project.transverse_layout=p.transverse_layout||{mode:'proposed',positions_mm:[],geometry_confirmed:false};result=null;selectedZ=zs()[0];dirty();render();message(migrated?'Проект открыт. Расчёт трещин переведён на CScore; выполните расчёт заново.':'');}catch(e){message(e.message);}finally{$('file').value='';}};
$('title').onchange=e=>{project.title=e.target.value;};$('kind').onchange=e=>{project.kind=e.target.value;project.calculation_context={...nativeContext(),sections:[],second_order_mode:'unspecified',anchorage_confirmed:false,circular_shear:null};if(project.kind==='wall')project.force_scale='per_metre';else if(project.transverse)project.transverse.mode='off';project.zones=[];project.transverse_zones=[];project.transverse_layout={mode:'proposed',positions_mm:[],geometry_confirmed:false};result=null;dirty();render();};
for(const id of ['force_scale','spacing_m','moment_sign','max_tier','materials_confirmed','eccentricity_confirmed'])$(id).onchange=e=>{project[id]=e.target.type==='checkbox'?e.target.checked:['spacing_m','moment_sign','max_tier'].includes(id)?Number(e.target.value):e.target.value;dirty();renderForms();};
$('phase').onchange=e=>{phase=e.target.value;renderCharts();renderInspection();};$('showQ').onchange=renderCharts;
$('table').onclick=()=>{$('tableText').value='z\tcase\tbasis\tn\tm\tq\tm_long\n'+project.rows.map(r=>['z','case','basis','n','m','q','m_long'].map(k=>r[k]??'').join('\t')).join('\n');$('tableDialog').showModal();};
$('applyTable').onclick=async()=>{try{const v=await api('table',{text:$('tableText').value});const p={...project,rows:v.rows,zones:[],transverse_zones:[],transverse_layout:{mode:'proposed',positions_mm:[],geometry_confirmed:false}};await api('preview',p);project.rows=v.rows;project.source={type:'manual'};project.calculation_context={...nativeContext(),sections:[],second_order_mode:'unspecified',anchorage_confirmed:false,circular_shear:null};selectedZ=zs()[0];project.zones=[];project.transverse_zones=[];project.transverse_layout={mode:'proposed',positions_mm:[],geometry_confirmed:false};dirty();render();$('tableDialog').close();message();}catch(e){message(e.message);}};
$('materials').onclick=()=>{$('materialText').value=JSON.stringify({materials:project.materials,sls_settings:project.sls_settings},null,2);$('materialDialog').showModal();};
$('applyMaterials').onclick=async()=>{try{const v=JSON.parse($('materialText').value),p={...project,materials:v.materials,sls_settings:v.sls_settings};await api('preview',p);project=p;project.materials_confirmed=false;dirty();render();$('materialDialog').close();message();}catch(e){message(e.message);}};
$('addZone').onclick=()=>{const z=zs();project.zones.push({top:z[0],bottom:z.at(-1),tiers:[2,project.kind==='wall'?2:1]});dirty();renderZones();};

$('connectionCheck').onclick=calculateConnection;
$('connectionSettings').onclick=()=>{
 const c=nativeContext();$('secondOrderMode').value=c.second_order_mode;
 $('circularShearFields').hidden=project.kind!=='pile';
 const e=c.circular_shear;$('spiralEnable').checked=!!e;
 $('circularShearMode').value=e?.method??'vniigs_1984_two_legs';
 $('spiralContributionFields').hidden=$('circularShearMode').value!=='vniigs_1984_user_spiral';
 $('spiralA').value=e?.a_mm??'';$('spiralWinding').value=e?.winding_diameter_mm??'';
 $('spiralEfficiency').value=e?.circular_efficiency??'';$('spiralBasis').value=e?.efficiency_basis??'';
 $('spiralConfirmed').checked=e?.confirmed===true;
 $('nativeL0x').value=c.l0_x_m??'';$('nativeL0y').value=c.l0_y_m??'';$('nativePsi').value=c.psi;
 $('nativeMoment').checked=c.inclined_moment_requested;$('nativeAnchorageConfirmed').checked=c.anchorage_confirmed;
 $('nativeAnchorageFactor').value=c.anchorage_factor??'';$('nativeSectionZ').textContent=fmt(selectedZ)+' м';
 const support=c.sections.find(s=>s.z===selectedZ);$('nativeSupportDown').value=support?.support_distance_positive_m??'';$('nativeSupportUp').value=support?.support_distance_negative_m??'';
 $('connectionDialog').dataset.z=selectedZ;$('connectionDialog').showModal();};
$('applyConnectionSettings').onclick=async()=>{try{
 const optional=id=>$(id).value.trim()===''?null:Number($(id).value),c=nativeContext(),z=Number($('connectionDialog').dataset.z);
 c.second_order_mode=$('secondOrderMode').value;c.l0_x_m=optional('nativeL0x');c.l0_y_m=optional('nativeL0y');c.psi=Number($('nativePsi').value);
 c.inclined_moment_requested=$('nativeMoment').checked;c.anchorage_confirmed=$('nativeAnchorageConfirmed').checked;c.anchorage_factor=optional('nativeAnchorageFactor');
 const manualSpiral=$('circularShearMode').value==='vniigs_1984_user_spiral';
 c.circular_shear=project.kind==='pile'&&$('spiralEnable').checked ? {
  method:$('circularShearMode').value,a_mm:optional('spiralA'),
  winding_diameter_mm:manualSpiral?optional('spiralWinding'):null,
  circular_efficiency:manualSpiral?optional('spiralEfficiency'):null,efficiency_basis:$('spiralBasis').value.trim(),confirmed:$('spiralConfirmed').checked} : null;
 c.sections=c.sections.filter(s=>s.z!==z);const down=optional('nativeSupportDown'),up=optional('nativeSupportUp');if(down!==null||up!==null)c.sections.push({z,support_distance_positive_m:down,support_distance_negative_m:up});
 const p={...project,calculation_context:c};await api('preview',p);project=p;dirty();render();$('connectionDialog').close();message();
 }catch(e){message(e.message);}};
$('spiralEnable').onchange=()=>{$('spiralConfirmed').checked=false;};
$('circularShearMode').onchange=()=>{$('spiralContributionFields').hidden=$('circularShearMode').value!=='vniigs_1984_user_spiral';$('spiralConfirmed').checked=false;};
for(const id of ['spiralA','spiralWinding','spiralEfficiency','spiralBasis'])$(id).oninput=()=>{$('spiralConfirmed').checked=false;};
$('design').onclick=()=>calculate(false);$('check').onclick=()=>calculate(true);document.querySelectorAll('[data-close]').forEach(b=>b.onclick=()=>b.closest('dialog').close());
$('plaxis').onclick=()=>$('plaxisDialog').showModal();$('connect').onclick=async()=>{try{catalog=null;$('objects').innerHTML='';$('phases').innerHTML='';$('extract').disabled=true;$('plaxisInfo').textContent='Подключение…';catalog=await api('plaxis/connect',{port:Number($('plaxisPort').value),password:$('plaxisPassword').value});$('plaxisPassword').value='';$('extract').disabled=false;$('objects').innerHTML=catalog.objects.map(o=>`<option value="${o.id}">${esc(o.name)} · ${esc(o.kind)}</option>`).join('');$('phases').innerHTML=catalog.phases.map(o=>`<option value="${o.id}">${esc(o.name)}</option>`).join('');$('plaxisInfo').textContent=Object.entries(catalog.info).map(([k,v])=>k+': '+v).join(' · ');}catch(e){$('plaxisInfo').textContent=e.message;}};
$('extract').disabled=true;$('extract').onclick=async()=>{try{if(!catalog)throw Error('Сначала подключитесь к Output');const v=await api('plaxis/extract',{objects:[...$('objects').selectedOptions].map(o=>+o.value),phases:[...$('phases').selectedOptions].map(o=>+o.value),basis:$('importBasis').value,force_unit:$('forceUnit').value,length_unit:$('lengthUnit').value,force_scale:$('importScale').value,confirmed:$('importConfirmed').checked,fully_sustained:$('sustained').checked});if($('appendRows').checked&&v.force_scale!==project.force_scale)throw Error('Масштаб текущей таблицы и импорта различается');const p={...project,rows:$('appendRows').checked?[...project.rows,...v.rows]:v.rows,force_scale:v.force_scale,zones:[],transverse_zones:[],transverse_layout:{mode:'proposed',positions_mm:[],geometry_confirmed:false}};await api('preview',p);project=p;project.source=v.source;project.zones=[];project.calculation_context={...nativeContext(),sections:[],second_order_mode:'unspecified',anchorage_confirmed:false,circular_shear:null};selectedZ=zs()[0];dirty();render();$('plaxisDialog').close();message();}catch(e){$('plaxisInfo').textContent=e.message;}};
api('default').then(p=>{project=p;selectedZ=zs()[2];render();refreshProfileAudit();}).catch(e=>message(e.message));
