'use strict';
const $=id=>document.getElementById(id),token=document.querySelector('meta[name=session]').content;
let project,result=null,stale=false,revision=0,selectedZ=0,phase='all',busy=false,catalog=null;
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=(v,n=2)=>Number.isFinite(v)?v.toLocaleString('ru-RU',{maximumFractionDigits:n}):'—';
function message(s=''){ $('message').hidden=!s;$('message').textContent=s;document.querySelectorAll('.dialogerror').forEach(x=>x.remove());const d=document.querySelector('dialog[open]');if(s&&d){const p=document.createElement('p');p.className='dialogerror notice';p.textContent=s;d.prepend(p);} }
async function api(path,data){const r=await fetch('/api/'+path,{method:data===undefined?'GET':'POST',headers:{'X-Session':token,'Content-Type':'application/json'},body:data===undefined?undefined:JSON.stringify(data)});const v=await r.json();if(!r.ok)throw Error(v.error||'Ошибка запроса');return v;}
function dirty(){revision++;stale=true;$('resultState').textContent=result?'Данные изменены · пересчитайте':'Расчёт не выполнен';renderCharts();renderInspection();}
const fieldsWall=[['panel_width_mm','Ширина захватки, мм'],['thickness_mm','Толщина стены, мм'],['cage_count','Число каркасов'],['cage_width_mm','Ширина каркаса, мм'],['end_cover_mm','Торцевой защитный слой, мм'],['cover_mm','Защитный слой до хомута, мм'],['ties_diameter_mm','Диаметр хомута, мм'],['min_clear_spacing_mm','Минимальный просвет, мм'],['min_cage_gap_mm','Промежуток каркасов, мм']];
const fieldsPile=[['diameter_mm','Диаметр сваи, мм'],['cover_mm','Защитный слой до хомута, мм'],['ties_diameter_mm','Диаметр хомута, мм'],['min_clear_spacing_mm','Минимальный просвет, мм']];
function inputField(label,value,attrs){return `<label>${esc(label)}<input type="number" value="${esc(value)}" ${attrs}></label>`;}
function renderForms(){
 $('title').value=project.title;$('kind').value=project.kind;$('force_scale').value=project.force_scale;$('spacing_m').value=project.spacing_m;
 $('moment_sign').value=project.moment_sign;$('max_tier').value=project.max_tier;
 $('materials_confirmed').checked=project.materials_confirmed;$('eccentricity_confirmed').checked=project.eccentricity_confirmed;
 const kind=project.kind,geom=project[kind];
 $('geometry').innerHTML=(kind==='wall'?fieldsWall:fieldsPile).map(([k,l])=>inputField(l,geom[k],`data-geom="${k}" min="0" step="1"`)).join('');
 $('reinforcement').innerHTML=kind==='wall'?['Грань y−','Грань y+'].map((f,i)=>inputField(`${f} · стержней фона на каркас`,project.background[i],`data-bg="${i}" min="2" step="1"`)+inputField(`${f} · диаметр, мм`,project.diameters[i],`data-dia="${i}" min="1"`)).join(''):inputField('Стержней фона в кольце',project.pile_background,'data-pile="pile_background" min="6" step="1"')+inputField('Диаметр стержня, мм',project.pile_diameter,'data-pile="pile_diameter" min="1"');
 $('spacingLabel').hidden=kind!=='pile'||project.force_scale!=='per_metre';
 document.querySelectorAll('[data-geom]').forEach(e=>e.onchange=()=>{project[kind][e.dataset.geom]=Number(e.value);dirty();});
 document.querySelectorAll('[data-bg]').forEach(e=>e.onchange=()=>{project.background[+e.dataset.bg]=Number(e.value);dirty();renderZones();});
 document.querySelectorAll('[data-dia]').forEach(e=>e.onchange=()=>{project.diameters[+e.dataset.dia]=Number(e.value);dirty();});
 document.querySelectorAll('[data-pile]').forEach(e=>e.onchange=()=>{project[e.dataset.pile]=Number(e.value);dirty();renderZones();});
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
function renderInspection(){if(!project)return;$('elevation').textContent=fmt(selectedZ)+' м';const t=tierAt(selectedZ);let html='<h3>Принятое армирование</h3>';
 for(let i=0;i<(project.kind==='wall'?2:1);i++){const bg=countAt(1,i),total=countAt(t[i],i),d=project.kind==='wall'?project.diameters[i]:project.pile_diameter;html+=metric(project.kind==='wall'?`Грань y${i?'+':'−'}`:'Кольцо',`${bg} + ${total-bg} Ø${d}`)+metric('As'+(project.kind==='wall'?'/м':''),fmt(area(t[i],i))+' см²');}
 const station=result&&!stale?result.stations.find(s=>s.z===selectedZ):null;
 if(station){const c=station.adopted;html+=`<div class="status ${station.checked?'':'bad'}">${station.checked?'Проверки сечения пройдены предварительно':'Сечение требует проверки'}</div>`;
 if(c){html+=metric('Прочность',fmt(c.max_utilization,3));const governing=c.loads?.filter(l=>Number.isFinite(l.utilization)).sort((a,b)=>b.utilization-a.utilization)[0];if(governing)html+=metric('Определяет',governing.id);
 html+=metric('Трещины',({'preliminary_below_limit':'предварительно проходят','preliminary_exceeds_limit':'превышение','engineering_review':'пограничный результат','not_evaluated_uls_fail':'пропущены: отказ ULS','not_evaluated_uls_indeterminate':'пропущены: ULS неизвестен','research_indeterminate':'неопределённо'})[c.sls_status]||'не запрошены');}
 else html+='<p class="hint">Нет геометрически допустимого варианта.</p>';}
 else html+=`<div class="status">${stale&&result?'Результат устарел. Перепроверьте принятое.':'Расчёт сечения не выполнен'}</div>`;
 html+='<h3>Совместные усилия</h3>';for(const r of rows().filter(r=>r.z===selectedZ&&(phase==='all'||r.basis+' · '+r.case===phase)))html+=`<p class="small">${esc(r.case)} · ${esc(r.basis)}</p>`+metric('N / M',fmt(r.n)+' / '+fmt(r.m));
 $('inspection').innerHTML=html;
}
function tierOptions(i,t){return [1,2,3].map(x=>`<option value="${x}" ${x===t?'selected':''}>${countAt(x,i)} ст.</option>`).join('');}
function renderZones(){if(!project.zones.length){$('zoneTable').innerHTML='<p class="empty">Усилений нет. Выполните подбор или добавьте участок вручную.</p>';return;}
 $('zoneTable').innerHTML='<table><thead><tr><th>Верх, м</th><th>Низ, м</th><th>Всего '+(project.kind==='wall'?'y−':'кольцо')+'</th>'+(project.kind==='wall'?'<th>Всего y+</th>':'')+'<th></th></tr></thead><tbody>'+project.zones.map((z,j)=>`<tr><td><input aria-label="Верх участка ${j+1}" type="number" step="0.1" value="${z.top}" data-zone="${j}" data-key="top"></td><td><input aria-label="Низ участка ${j+1}" type="number" step="0.1" value="${z.bottom}" data-zone="${j}" data-key="bottom"></td><td><select aria-label="Армирование первой грани" data-zone="${j}" data-tier="0">${tierOptions(0,z.tiers[0])}</select></td>${project.kind==='wall'?`<td><select aria-label="Армирование второй грани" data-zone="${j}" data-tier="1">${tierOptions(1,z.tiers[1])}</select></td>`:''}<td><button data-delete="${j}" title="Удалить участок">×</button></td></tr>`).join('')+'</tbody></table>';
 document.querySelectorAll('[data-zone]').forEach(e=>e.onchange=()=>{let z=project.zones[+e.dataset.zone];if(e.dataset.key)z[e.dataset.key]=+e.value;else z.tiers[+e.dataset.tier]=+e.value;dirty();});
 document.querySelectorAll('[data-delete]').forEach(e=>e.onclick=()=>{project.zones.splice(+e.dataset.delete,1);dirty();renderZones();});
}
function render(){renderForms();renderCharts();renderZones();renderInspection();}
async function calculate(recheck){if(busy)return;message();const rev=revision;busy=true;document.body.classList.add('busy');
 try{const start=await api('calculate',{project,recheck});while(true){await new Promise(r=>setTimeout(r,600));const job=await api('job?id='+encodeURIComponent(start.job));$('progress').textContent=job.message;if(job.status==='error')throw Error(job.message);if(job.status==='done'){if(revision!==rev){message('Во время расчёта данные изменились. Результат не применён; запустите расчёт заново.');break;}result=job.result;project.zones=result.zones;stale=false;$('resultState').textContent=result.all_checked?'Проверено по заданным отметкам':'Есть непрошедшие сечения';renderZones();renderCharts();renderInspection();break;}}}
 catch(e){message(e.message);}finally{busy=false;document.body.classList.remove('busy');$('progress').textContent='Локальный расчёт · данные остаются на компьютере';}}
function save(){const allowed=['version','title','kind','force_scale','spacing_m','moment_sign','materials_confirmed','eccentricity_confirmed','background','pile_background','diameters','pile_diameter','max_tier','wall','pile','materials','sls_settings','rows','zones','source'];const clean=Object.fromEntries(allowed.map(k=>[k,project[k]]));const blob=new Blob([JSON.stringify(clean,null,2)],{type:'application/json'}),a=document.createElement('a');a.href=URL.createObjectURL(blob);a.download=(project.title||'Расчёт').replace(/[<>:"/\\|?*]/g,'_')+'.json';a.click();setTimeout(()=>URL.revokeObjectURL(a.href),1000);}
$('save').onclick=save;$('open').onclick=()=>$('file').click();$('file').onchange=async e=>{try{const p=JSON.parse(await e.target.files[0].text());await api('preview',p);project=p;project.zones=Array.isArray(p.zones)?p.zones:[];result=null;selectedZ=zs()[0];dirty();render();message();}catch(e){message(e.message);}finally{$('file').value='';}};
$('title').onchange=e=>{project.title=e.target.value;};$('kind').onchange=e=>{project.kind=e.target.value;if(project.kind==='wall')project.force_scale='per_metre';project.zones=[];result=null;dirty();render();};
for(const id of ['force_scale','spacing_m','moment_sign','max_tier','materials_confirmed','eccentricity_confirmed'])$(id).onchange=e=>{project[id]=e.target.type==='checkbox'?e.target.checked:['spacing_m','moment_sign','max_tier'].includes(id)?Number(e.target.value):e.target.value;dirty();renderForms();};
$('phase').onchange=e=>{phase=e.target.value;renderCharts();renderInspection();};$('showQ').onchange=renderCharts;
$('table').onclick=()=>{$('tableText').value='z\tcase\tbasis\tn\tm\tq\tm_long\n'+project.rows.map(r=>['z','case','basis','n','m','q','m_long'].map(k=>r[k]??'').join('\t')).join('\n');$('tableDialog').showModal();};
$('applyTable').onclick=async()=>{try{const v=await api('table',{text:$('tableText').value});const p={...project,rows:v.rows,zones:[]};await api('preview',p);project.rows=v.rows;project.source={type:'manual'};selectedZ=zs()[0];project.zones=[];dirty();render();$('tableDialog').close();message();}catch(e){message(e.message);}};
$('materials').onclick=()=>{$('materialText').value=JSON.stringify({materials:project.materials,sls_settings:project.sls_settings},null,2);$('materialDialog').showModal();};
$('applyMaterials').onclick=async()=>{try{const v=JSON.parse($('materialText').value),p={...project,materials:v.materials,sls_settings:v.sls_settings};await api('preview',p);project=p;project.materials_confirmed=false;dirty();render();$('materialDialog').close();message();}catch(e){message(e.message);}};
$('addZone').onclick=()=>{const z=zs();project.zones.push({top:z[0],bottom:z.at(-1),tiers:[2,project.kind==='wall'?2:1]});dirty();renderZones();};
$('design').onclick=()=>calculate(false);$('check').onclick=()=>calculate(true);document.querySelectorAll('[data-close]').forEach(b=>b.onclick=()=>b.closest('dialog').close());
$('plaxis').onclick=()=>$('plaxisDialog').showModal();$('connect').onclick=async()=>{try{catalog=null;$('objects').innerHTML='';$('phases').innerHTML='';$('extract').disabled=true;$('plaxisInfo').textContent='Подключение…';catalog=await api('plaxis/connect',{port:Number($('plaxisPort').value),password:$('plaxisPassword').value});$('plaxisPassword').value='';$('extract').disabled=false;$('objects').innerHTML=catalog.objects.map(o=>`<option value="${o.id}">${esc(o.name)} · ${esc(o.kind)}</option>`).join('');$('phases').innerHTML=catalog.phases.map(o=>`<option value="${o.id}">${esc(o.name)}</option>`).join('');$('plaxisInfo').textContent=Object.entries(catalog.info).map(([k,v])=>k+': '+v).join(' · ');}catch(e){$('plaxisInfo').textContent=e.message;}};
$('extract').disabled=true;$('extract').onclick=async()=>{try{if(!catalog)throw Error('Сначала подключитесь к Output');const v=await api('plaxis/extract',{objects:[...$('objects').selectedOptions].map(o=>+o.value),phases:[...$('phases').selectedOptions].map(o=>+o.value),basis:$('importBasis').value,force_unit:$('forceUnit').value,length_unit:$('lengthUnit').value,force_scale:$('importScale').value,confirmed:$('importConfirmed').checked,fully_sustained:$('sustained').checked});if($('appendRows').checked&&v.force_scale!==project.force_scale)throw Error('Масштаб текущей таблицы и импорта различается');const p={...project,rows:$('appendRows').checked?[...project.rows,...v.rows]:v.rows,force_scale:v.force_scale,zones:[]};await api('preview',p);project=p;project.source=v.source;project.zones=[];selectedZ=zs()[0];dirty();render();$('plaxisDialog').close();message();}catch(e){$('plaxisInfo').textContent=e.message;}};
api('default').then(p=>{project=p;selectedZ=zs()[2];render();}).catch(e=>message(e.message));
