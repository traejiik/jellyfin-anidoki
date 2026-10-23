(async () => {
 if (!location.hostname.match(/^(localhost|127\.0\.0\.1)$/)) return;
 const marker = document.querySelector('#anidoki-notification-bootstrap');
 if (!marker) return;
 const {startNotifications} = await import(marker.src);
 for (let i = 0; i < 120 && !window.ApiClient; i++) await new Promise(r => setTimeout(r, 500));
 if (!window.ApiClient) return;
 window.__anidokiNotifications?.dispose();
 let pending = [], sequence = 0;
 const manual = new Map();
 const response = (body, status = 200) => ({ok: status >= 200 && status < 300, status, json: async () => structuredClone(body)});
 const transition = (operation, state, reason = '') => {
  operation.state = state; operation.reason = reason; operation.revision++;
 };
 const fixtureWindow = {
  ApiClient: window.ApiClient,
  // Every notification request is intercepted here; fixtures never call the server or trackers.
  fetch: async (input, options = {}) => {
   const path = new URL(input, location.href).pathname;
   if (path.endsWith('/manual-marks/capability')) return response({});
   if (path.endsWith('/manual-marks')) {
    for (const operation of manual.values()) {
     if (operation.state === 'Pending' && Date.parse(operation.expiresUtc) <= Date.now()) transition(operation, 'Expired', 'Visual confirmation expired.');
    }
    return response({manualUpdatesEnabled:true, operations:[...manual.values()]});
   }
   const command = path.match(/\/manual-marks\/([^/]+)\/(confirm|dismiss)$/);
   if (command) {
    const operation = manual.get(command[1]);
    if (!operation) return response({}, 404);
    if (JSON.parse(options.body || '{}').expectedRevision !== operation.revision) return response(operation, 409);
    if (operation.state !== 'Pending') return response(operation);
    if (command[2] === 'dismiss') transition(operation, 'Dismissed', 'Visual fixture dismissed. No tracker update sent.');
    else {
     transition(operation, 'Sending', 'Simulated update; no requests are sent.');
     window.setTimeout(() => {
      if (operation.state === 'Sending') transition(operation, 'Confirmed', 'Visual fixture complete. No tracker update sent.');
      document.dispatchEvent(new Event('viewshow'));
     }, 2500);
    }
    return response(operation);
   }
   return response({enabled:true,reset:false,cursor:'visual:'+sequence,events:pending.splice(0)});
  },
  addEventListener:window.addEventListener.bind(window),
  setTimeout:window.setTimeout.bind(window),clearTimeout:window.clearTimeout.bind(window),
  setInterval:window.setInterval.bind(window),clearInterval:window.clearInterval.bind(window)
 };
 startNotifications(fixtureWindow,document);
 const panel=document.createElement('div');panel.id='anidoki-test-fixture-controls';
 panel.style.cssText='position:fixed;bottom:16px;left:16px;z-index:2000;display:flex;gap:8px;flex-wrap:wrap;padding:12px;background:#283445;color:white;max-width:calc(100vw - 56px);border-radius:12px;font:inherit';
 const cases={
  Episode:{title:'Frieren: Beyond Journey’s End',episode:12,outcomes:[['AniList',12,'Watching'],['Mal',12,'Watching']]},
  Partial:{title:'Frieren: Beyond Journey’s End',episode:12,outcomes:[['AniList',12,'Watching'],['Mal',null,null,'Unconfirmed']]},
  Mapped:{title:'A very long anime title — The next journey beyond the mountains',episode:13,outcomes:[['AniList',1,'Watching','Confirmed','Second season'],['Mal',13,'Completed']]},
  Annict:{title:'Frieren: Beyond Journey’s End',episode:12,outcomes:[['Annict',null,'Watching']]},
  Movie:{title:'Your Name',episode:null,movie:true,outcomes:[['AniList',1,'Completed'],['Mal',1,'Completed']]}
 };
 for (const [label,data] of Object.entries(cases)) {
  const b=document.createElement('button');b.type='button';b.textContent='Test '+label;b.onclick=()=>{
   const id='fixture-'+(++sequence);
   pending.push({eventId:id,operationId:id,createdUtc:new Date().toISOString(),sourceTitle:data.title,sourceEpisode:data.episode,isMovie:!!data.movie,
    outcomes:data.outcomes.map(([provider,progress,status,kind='Confirmed',targetTitle=data.title])=>({provider,targetId:provider,progress,status,kind,targetTitle,statusOnly:provider==='Annict',meaningfulChange:kind==='Confirmed'}))});
   document.dispatchEvent(new Event('viewshow'));
  };panel.append(b);
 }
 const confirmation=document.createElement('button');confirmation.type='button';confirmation.textContent='Test confirmation';
 confirmation.onclick=()=>{
  // Replace any earlier manual fixture so repeated clicks produce one reviewable prompt.
  for (const operation of manual.values()) if (['Pending','Queued','Sending'].includes(operation.state)) transition(operation,'Dismissed','Replaced by another visual fixture.');
  const id=crypto.randomUUID();
  manual.set(id,{operationId:id,revision:1,sourceItemId:'fixture-only',sourceTitle:'Frieren: Beyond Journey’s End',
   sourceEpisode:12,episodeTitle:'The Real Hero',state:'Pending',expiresUtc:new Date(Date.now()+120000).toISOString(),outcomes:[],reason:''});
  document.dispatchEvent(new Event('viewshow'));
 };panel.prepend(confirmation);
 const pause=document.createElement('button');pause.textContent='Pause fixture countdown';pause.onclick=()=>document.querySelectorAll('.doki-toast').forEach(x=>x.dispatchEvent(new Event('mouseenter')));panel.append(pause);
 const fullscreen=document.createElement('button');fullscreen.textContent='Test container fullscreen';
 fullscreen.onclick=async()=>{
  const frame=document.createElement('section');frame.id='anidoki-fixture-fullscreen';frame.style.cssText='background:var(--jf-palette-background-default,#18202d);width:100%;height:100%;color:white';
  const title=document.createElement('h1');title.textContent='Local fullscreen fixture';frame.append(title,panel);document.body.append(frame);await frame.requestFullscreen();
 };
 const exit=document.createElement('button');exit.textContent='Exit fixture fullscreen';exit.onclick=()=>document.exitFullscreen();
 const video=document.createElement('video');video.id='anidoki-fixture-video';video.controls=true;video.style.cssText='width:120px;height:60px';
 const videoFull=document.createElement('button');videoFull.textContent='Test video fullscreen';videoFull.onclick=()=>video.requestFullscreen();
 const hide=document.createElement('button');hide.textContent='Hide fixture controls';hide.onclick=()=>{panel.hidden=true;panel.style.display='none';};
 panel.append(fullscreen,videoFull,exit,hide,video);
 document.addEventListener('fullscreenchange',()=>{if(!document.fullscreenElement){document.body.append(panel);document.querySelector('#anidoki-fixture-fullscreen')?.remove();}});
 document.body.append(panel);
})();
