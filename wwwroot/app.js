function connectionBar(g,x,y,w,h,online){
  g.save();
  g.fillStyle=css(online?'--connected':'--red');g.fillRect(x,y,w,h);
  if(!online&&w>2&&h>2){
    g.beginPath();g.rect(x,y,w,h);g.clip();
    g.strokeStyle='#172033';g.lineWidth=1.5;g.setLineDash([]);
    g.beginPath();for(let offset=-h;offset<w;offset+=7){g.moveTo(x+offset,y+h);g.lineTo(x+offset+h,y)}g.stroke();
  }
  g.restore();
}

let monitorSessionId=null;
function scheduledLink(a,b){
  const at=new Date(a.timestamp).getTime(),bt=new Date(b.timestamp).getTime();
  if(a.sessionId||b.sessionId)
    return !!a.sessionId&&a.sessionId===b.sessionId&&bt>=at&&bt<=new Date(a.nextCheckAt).getTime()+30000;
  return bt>=at&&bt-at<=25000;
}
function connectionPeriods(items,cut,now){
  const samples=items.filter(s=>Number.isFinite(new Date(s.timestamp).getTime())&&new Date(s.timestamp)<=now&&typeof s.online==='boolean')
    .sort((a,b)=>new Date(a.timestamp)-new Date(b.timestamp));
  const periods=[];let active=null;
  for(let i=0;i<samples.length;i++){
    const s=samples[i],next=samples[i+1],at=new Date(s.timestamp).getTime();
    const linked=next&&scheduledLink(s,next);
    if(!active) {
      active={start:at,end:at,online:s.online,complete:false,current:false,partial:i===0||!scheduledLink(samples[i-1],s),samples:[]};
      periods.push(active);
    }
    active.samples.push(s);
    active.end=linked?new Date(next.timestamp).getTime():at;
    if(linked&&next.online!==s.online){active.complete=!active.partial;active=null}
    else if(!linked){
      const live=s.sessionId?s.sessionId===monitorSessionId&&now<=new Date(s.nextCheckAt).getTime()+30000:now-at<=25000;
      if(!next&&live){active.end=now;active.current=true}
      active=null;
    }
  }
  return periods.filter(p=>p.end>=cut).map(p=>({...p,
    clipped:p.start<cut,start:Math.max(p.start,cut),duration:Math.max(0,p.end-Math.max(p.start,cut))}));
}
function cycleChart(items){
  const now=Date.now(),hours=+$('range').value,cut=now-hours*3600000;
  const periods=connectionPeriods(items,cut,now),current=periods.find(p=>p.current);
  const upPeriods=periods.filter(p=>p.online);
  $('longestUptime').textContent=upPeriods.length?elapsed(Math.max(...upPeriods.map(p=>p.duration))):'—';
  $('cycleCurrent').textContent=current?`${current.online?'Connected':'Offline'} for: ${elapsed(current.duration)}${current.clipped?' (in range)':''}`:'Current state: awaiting fresh check';
  for(const [online,id] of [[true,'averageUp'],[false,'averageDown']]){
    const complete=periods.filter(p=>p.online===online&&p.complete&&!p.clipped);
    $(id).textContent=complete.length?elapsed(complete.reduce((n,p)=>n+p.duration,0)/complete.length):'—';
    $(online?'intervalAverageUp':'intervalAverageDown').textContent=$(id).textContent;
  }
  const c=$('cycleChart'),[g,w,h,d]=size(c);g.scale(d,d);
  const left=48,bottom=h-20,top=12,plotH=Math.max(1,bottom-top),plotW=Math.max(1,w-left-8);
  const max=Math.max(1000,...periods.map(p=>p.duration)),x=t=>left+(t-cut)/(now-cut)*plotW;
  g.font='10px system-ui';g.fillStyle=css('--muted');
  for(let i=0;i<=2;i++){const y=bottom-plotH*i/2;g.strokeStyle=css('--line');g.beginPath();g.moveTo(left,y);g.lineTo(w-8,y);g.stroke();g.fillText(elapsed(max*i/2),0,y+3)}
  for(const p of periods){const height=p.duration/max*plotH;g.globalAlpha=p.current?1:.85;connectionBar(g,x(p.start),bottom-height,Math.max(.5,x(p.end)-x(p.start)-1),height,p.online)}
  g.globalAlpha=1;g.fillStyle=css('--muted');g.fillText(`${hours}h ago`,left,h-3);g.fillText('Now',w-28,h-3);
  c.onmousemove=e=>{const t=cut+(e.offsetX-left)/plotW*(now-cut),p=periods.find(p=>t>=p.start&&t<=p.end);c.title=p?`${p.online?'Connected':'Offline'}: ${elapsed(p.duration)} | ${new Date(p.start).toLocaleString()} — ${new Date(p.end).toLocaleTimeString()}${p.current?' (ongoing)':p.complete?'':' (partial observation)'}`:'No observed period'};
}
const $=id=>document.getElementById(id), css=n=>getComputedStyle(document.documentElement).getPropertyValue(n).trim();
function size(c){const d=devicePixelRatio||1,r=c.getBoundingClientRect();c.width=r.width*d;c.height=r.height*d;return[c.getContext('2d'),r.width,r.height,d]}
function axes(g,w,h,d,max,rightMax){g.scale(d,d);g.strokeStyle=css('--line');g.fillStyle=css('--muted');g.font='11px system-ui';for(let i=0;i<5;i++){let y=15+(h-40)*i/4;g.beginPath();g.moveTo(42,y);g.lineTo(w-42,y);g.stroke();g.fillText(Math.round(max*(4-i)/4),5,y+4);g.fillText(Math.round(rightMax*(4-i)/4),w-34,y+4)}g.fillText('Mbps',4,10);g.fillText('ms',w-22,10)}
function outageWindows(items){return connectionPeriods(items,-Infinity,Date.now()).filter(p=>!p.online)}
function speedHoverTarget(points,bands,px,py){
  let nearest=null,distance=12;
  for(const point of points){
    const delta=Math.hypot(point.x-px,point.y-py);
    if(delta<=distance){nearest=point;distance=delta}
  }
  return nearest||bands.find(b=>px>=b.left&&px<=b.right&&py>=b.top&&py<=b.bottom)||null;
}
function speedChart(items,connectivity){
  const c=$('speedChart'),[g,w,h,d]=size(c),hours=+$('range').value,now=Date.now(),cut=now-hours*3600000;
  const data=items.filter(s=>new Date(s.timestamp)>=cut&&new Date(s.timestamp)<=now),points=[],bands=[];
  let tooltip=$('speedTooltip');
  if(!tooltip){
    tooltip=document.createElement('div');tooltip.id='speedTooltip';tooltip.className='chart-tooltip';
    tooltip.setAttribute('role','tooltip');document.body.appendChild(tooltip);
  }
  const hide=()=>{tooltip.hidden=true;c.style.cursor='default'};
  hide();
  const date=t=>new Intl.DateTimeFormat(undefined,{weekday:'short',year:'numeric',month:'short',day:'numeric',
    hour:'2-digit',minute:'2-digit',second:'2-digit',timeZoneName:'short'}).format(new Date(t));
  const actual=(s,key)=>s[key]!=null&&Number.isFinite(Number(s[key]));
  const measurements=[['downloadMbps','Download','Mbps','--blue'],['uploadMbps','Upload','Mbps','--green'],
    ['latencyMs','Latency','ms','--amber'],['jitterMs','Jitter','ms','--purple']];
  const details=s=>measurements.map(([key,label,unit])=>`${label}: ${actual(s,key)?Number(s[key]).toFixed(2)+' '+unit:'Not collected'}`).join('\n');
  const testText=s=>`${date(s.timestamp)}\n${s.success?'Completed speed test':'Failed speed test'+(s.failedPhase?' · '+s.failedPhase:'')}\n${details(s)}${s.durationSeconds!=null?'\nTest duration: '+Number(s.durationSeconds).toFixed(1)+' seconds':''}${s.error?'\n'+s.error:''}`;
  g.clearRect(0,0,c.width,c.height);
  const max=Math.max(10,...data.flatMap(s=>['downloadMbps','uploadMbps'].filter(k=>actual(s,k)).map(k=>Number(s[k]))))*1.1;
  const rightMax=Math.max(50,...data.flatMap(s=>['latencyMs','jitterMs'].filter(k=>actual(s,k)).map(k=>Number(s[k]))))*1.1;
  axes(g,w,h,d,max,rightMax);
  const x=t=>42+(new Date(t)-cut)/(hours*3600000)*(w-84),y=v=>15+(h-40)*(1-v/max),yr=v=>15+(h-40)*(1-v/rightMax);
  for(const outage of outageWindows(connectivity).filter(o=>o.end>=cut)){
    const left=Math.max(42,x(Math.max(cut,outage.start))),right=Math.min(w-42,Math.max(left+2,x(outage.end)));
    const last=outage.samples.at(-1);
    g.fillStyle=css('--red')+'55';g.fillRect(left,15,right-left,h-40);
    bands.push({left,right,top:15,bottom:h-25,text:`Connectivity failure\nDetected: ${date(outage.start)}\n${outage.current?'Ongoing as of':outage.complete?'Recovery detected':'Last covered time'}: ${date(outage.end)}\nEstimated duration: ${Math.round(outage.duration/1000)} seconds${last?.failureType?'\nFailure: '+last.failureType:''}${last?.probeEndpoint?'\nEndpoint: '+last.probeEndpoint:''}${last?.error?'\n'+last.error:''}\nBased on recorded checks; intervals may include scheduled sleeps.`});
  }
  for(const s of data.filter(s=>!s.success)){
    const left=x(s.timestamp)-2;g.fillStyle=css('--red')+'aa';g.fillRect(left,15,4,h-40);
    bands.unshift({left:left-3,right:left+7,top:15,bottom:h-25,text:testText(s)});
  }
  for(const [key,label,unit,color] of measurements){
    const scale=unit==='Mbps'?y:yr;
    g.setLineDash(key==='jitterMs'?[6,4]:[]);g.strokeStyle=css(color);g.lineWidth=2;
    for(let i=1;i<data.length;i++){
      const a=data[i-1],b=data[i];
      if(!actual(a,key)||!actual(b,key))continue;
      g.beginPath();g.moveTo(x(a.timestamp),scale(Number(a[key])));g.lineTo(x(b.timestamp),scale(Number(b[key])));g.stroke();
    }
    g.setLineDash([]);g.fillStyle=css(color);
    for(const s of data){
      if(!actual(s,key))continue;
      const px=x(s.timestamp),py=scale(Number(s[key]));
      g.beginPath();g.arc(px,py,3,0,Math.PI*2);g.fill();
      points.push({x:px,y:py,text:`${label}: ${Number(s[key]).toFixed(2)} ${unit}\n${testText(s)}`});
    }
  }
  c.onpointermove=e=>{
    const rect=c.getBoundingClientRect(),px=(e.clientX-rect.left)*w/rect.width,py=(e.clientY-rect.top)*h/rect.height;
    const hit=speedHoverTarget(points,bands,px,py);
    if(!hit){hide();return}
    tooltip.textContent=hit.text;tooltip.hidden=false;c.style.cursor='crosshair';
    const box=tooltip.getBoundingClientRect(),margin=8;
    let left=e.clientX+14,top=e.clientY+14;
    if(left+box.width>innerWidth-margin)left=e.clientX-box.width-14;
    if(top+box.height>innerHeight-margin)top=e.clientY-box.height-14;
    tooltip.style.left=Math.max(margin,left)+'px';tooltip.style.top=Math.max(margin,top)+'px';
  };
  c.onpointerleave=hide;c.onpointercancel=hide;
}
function uptimeChart(items){
  const c=$('uptimeChart'),[g,w,h,d]=size(c),hours=+$('range').value,now=Date.now(),cut=now-hours*3600000;
  const periods=connectionPeriods(items,cut,now),barY=6,barHeight=20,x=t=>(t-cut)/(now-cut)*w;
  g.scale(d,d);g.fillStyle=css('--unobserved');g.fillRect(0,barY,w,barHeight);
  g.font='bold 10px system-ui';
  for(const p of periods){
    const left=x(p.start),width=x(p.end)-left;
    connectionBar(g,left,barY,width,barHeight,p.online);
    const label=String(Math.round(p.duration/1000)),center=left+width/2;
    // Each status has a fixed row; allow labels to extend beyond narrow bars.
    const labelY=barY+barHeight+(p.online?18:42);
    g.save();g.strokeStyle=g.fillStyle=css(p.online?'--connected':'--red');
    g.globalAlpha=.45;g.beginPath();g.moveTo(center,barY+barHeight);
    g.lineTo(center,labelY-7);g.stroke();g.globalAlpha=1;
    const halfLabel=g.measureText(label).width/2;
    const labelX=Math.max(halfLabel,Math.min(w-halfLabel,center));
    g.textAlign='center';g.textBaseline='middle';g.fillText(label,labelX,labelY);g.restore();
  }
  g.textAlign='left';g.textBaseline='alphabetic';g.fillStyle=css('--muted');g.font='10px system-ui';
  g.fillText(`${hours} hours ago`,0,h-2);g.fillText('Now',w-22,h-2);
  c.onmousemove=e=>{
    const t=cut+e.offsetX/w*(now-cut),p=periods.find(p=>t>=p.start&&t<=p.end);
    c.title=p?`${p.online?'Connected':'Outage'}: ${Math.round(p.duration/1000)} seconds (estimated)${p.current?' — ongoing':''}${p.clipped?' — visible portion':!p.complete&&!p.current?' — partial observation':''}`:'No observations';
  };
}
function duration(ms){const seconds=Math.max(1,Math.round(ms/1000));return seconds<60?`${seconds}s`:`${Math.floor(seconds/60)}m ${seconds%60}s`}
function elapsed(ms){const seconds=Math.max(0,Math.round(ms/1000));if(seconds<60)return`${seconds}s`;const minutes=Math.floor(seconds/60);if(minutes<60)return`${minutes}m ${seconds%60}s`;const hours=Math.floor(minutes/60);return hours<24?`${hours}h ${minutes%60}m`:`${Math.floor(hours/24)}d ${hours%24}h`}
function renderReliability(items){
  const now=Date.now(),cut=now-Number($('range').value)*3600000,periods=connectionPeriods(items,cut,now);
  const online=periods.filter(p=>p.online).reduce((n,p)=>n+p.duration,0),offline=periods.filter(p=>!p.online).reduce((n,p)=>n+p.duration,0);
  const observed=online+offline,windows=periods.filter(p=>!p.online),current=periods.find(p=>p.current);
  $('uptimePercent').textContent=observed?`${(online/observed*100).toFixed(2)}%`:'—';
  $('connectedTime').textContent=observed?`${elapsed(online)} (${(online/observed*100).toFixed(2)}%)`:'—';
  $('outageTime').textContent=observed?`${elapsed(offline)} (${(offline/observed*100).toFixed(2)}%)`:'—';
  $('timelineRange').textContent=$('range').selectedOptions[0].textContent;
  $('timelineCoverage').textContent=`Estimated coverage: ${elapsed(observed)} of ${elapsed(now-cut)} (${(observed/(now-cut)*100).toFixed(1)}%). Scheduled sleeps are bridged; collection gaps excluded.`;
  $('outageCount').textContent=windows.length;
  $('longestOutage').textContent=windows.length?elapsed(Math.max(...windows.map(p=>p.duration))):'—';
  $('currentStreak').textContent=current?`${current.online?'Up':'Down'} ${elapsed(current.duration)}`:'—';
}
function renderOutages(connectivity){const body=$('outages'),available=body.closest('.table-wrap')?.clientHeight||450,limit=Math.max(1,Math.ceil(available/20)),ordered=[...connectivity].sort((a,b)=>new Date(a.timestamp)-new Date(b.timestamp)),windows=outageWindows(ordered).reverse().slice(0,limit),quality=s=>s&&s.latencyMs!=null?`${Number(s.latencyMs).toFixed(0)} / ${s.jitterMs==null?'—':Number(s.jitterMs).toFixed(0)} ms`:'—';body.replaceChildren();if(!windows.length){const row=body.insertRow(),cell=row.insertCell();cell.colSpan=8;cell.textContent='No outages recorded';return}for(const outage of windows){const sample=outage.samples[outage.samples.length-1],before=[...ordered].reverse().find(s=>s.online&&new Date(s.timestamp).getTime()<outage.start&&outage.start-new Date(s.timestamp).getTime()<=60000),recovery=ordered.find(s=>s.online&&new Date(s.timestamp).getTime()>=outage.end&&new Date(s.timestamp).getTime()-outage.end<=60000),row=body.insertRow();const values=[new Date(outage.start).toLocaleTimeString(),duration(outage.end-outage.start),sample.failureType||'unknown',sample.dnsResolved==null?'—':sample.dnsResolved?'OK':'Failed',sample.tcp443Connected==null?'—':sample.tcp443Connected?'OK':'Failed',quality(before),quality(recovery),sample.error||'No detail'];values.forEach((value,index)=>{const cell=row.insertCell();cell.textContent=value;if((index===3||index===4)&&value==='Failed')cell.className='bad'});row.title=`Started: ${new Date(outage.start).toLocaleString()}\nBefore L/J: ${quality(before)}${before?' at '+new Date(before.timestamp).toLocaleTimeString():''}\nRecovery L/J: ${quality(recovery)}${recovery?' at '+new Date(recovery.timestamp).toLocaleTimeString():''}\nEndpoint: ${sample.probeEndpoint||'—'}\nDNS addresses: ${sample.dnsAddresses||'—'}\n${sample.error||''}`;if(body.closest('.table-wrap').scrollHeight>available){row.remove();break}}}
let refreshVersion=0;
async function refresh(){const version=++refreshVersion,hours=Number($('range').value);try{const [status,speeds,connectivity]=await Promise.all(['/api/status',`/api/speed-tests?hours=${hours}`,`/api/connectivity?hours=${hours}`].map(u=>fetch(u).then(r=>{if(!r.ok)throw new Error(`HTTP ${r.status}`);return r.json()})));if(version!==refreshVersion)return;monitorSessionId=status.monitorSessionId;const c=status.connectivity,s=status.speedTest,i=status.connectionIdentity,l=status.localConnection;$('connection').textContent=c?.online?'Online':'Offline';$('lastCheck').textContent=c?`Checked ${new Date(c.timestamp).toLocaleTimeString()}`:'Waiting';$('nextCheck').textContent=status.nextConnectivityCheck?`Next: ${new Date(status.nextConnectivityCheck).toLocaleTimeString()}`:'Checking…';$('badge').textContent=c?.online?'Monitoring':'Outage detected';$('badge').style.background=c?.online?'#174b3d':'#672b36';$('down').textContent=s?.downloadMbps?.toFixed(1)??'—';$('up').textContent=s?.uploadMbps?.toFixed(1)??'—';$('latency').textContent=s?.latencyMs?.toFixed(0)??'—';$('jitter').textContent=s?.jitterMs?.toFixed(0)??'—';$('testStatus').textContent=s?.success?'Latest full test':s?.failedPhase?`Failed during ${s.failedPhase}`:'Waiting for first test';$('provider').textContent=i?.organization??(i?.error?'Unavailable':'Detecting…');$('publicIp').textContent=i?.publicIp??'—';$('providerLocation').textContent=[i?.city,i?.region,i?.country].filter(Boolean).join(', ')||(i?.error??'Public connection identity');$('identitySource').textContent=`Source: ${i?.source??'ipinfo.io'}${i?.timestamp?' · '+new Date(i.timestamp).toLocaleString():''}`;$('connectionType').textContent=l?.type??'Unknown';$('connectionAdapter').textContent=[l?.interfaceName,l?.localIp,l?.linkSpeedMbps?`${l.linkSpeedMbps} Mbps link`:null].filter(Boolean).join(' · ')||'Active network interface unavailable';speedChart(speeds,connectivity);uptimeChart(connectivity);renderReliability(connectivity);cycleChart(connectivity);renderOutages(connectivity)}catch(e){if(version===refreshVersion)$('badge').textContent='Dashboard error'}}
$('range').onchange=refresh;addEventListener('resize',refresh);refresh();setInterval(refresh,10000);
