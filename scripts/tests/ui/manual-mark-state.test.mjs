import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';
const { createManualMarkState, buildManualMarkPresentation } = await importConfiguration('ManualMarkStateJs.js');
test('newer sending revision replaces pending without accepting a stale response',()=>{
 const state=createManualMarkState(); state.upsert({operationId:'own',revision:1,state:'Pending'});state.upsert({operationId:'own',revision:2,state:'Sending'});state.upsert({operationId:'own',revision:1,state:'Pending'});
 assert.equal(state.operations.get('own').state,'Sending');assert.equal(state.operations.size,1);
});
test('pending operations have priority and are never evicted by result volume',()=>{
 const state=createManualMarkState();for(let i=0;i<12;i++)state.upsert({operationId:'pending'+i,revision:1,state:'Pending'});
 for(let i=0;i<20;i++)state.upsert({operationId:'result'+i,revision:1,state:'Confirmed'});
 assert.equal(state.operations.size,32); assert.equal(state.visible()[0].state,'Pending'); assert.equal(state.visible().length,2);
});
test('only terminal cards have timers and sending uses a spinner',()=>{
 for(const s of ['Pending','Queued','Sending'])assert.equal(buildManualMarkPresentation({state:s}).terminal,false);
 assert.equal(buildManualMarkPresentation({state:'Sending'}).spinner,true);
 assert.equal(buildManualMarkPresentation({state:'Unconfirmed'}).heading,'Tracker response unconfirmed');
 assert.equal(buildManualMarkPresentation({state:'Confirmed'}).terminal,true);
});
test('identity reset removes operations and dismissal never claims rollback',()=>{
 const state=createManualMarkState();state.upsert({operationId:'op',revision:1,state:'Sending'});state.hide('op');assert.equal(state.visible().length,0);state.clear();assert.equal(state.operations.size,0);
 assert.match(buildManualMarkPresentation({state:'Sending'}).detail,/already started/);
});

test('retained terminal history is bounded without discarding active confirmations',()=>{
 const state=createManualMarkState();
 state.upsert({operationId:'pending',revision:1,state:'Pending'});
 for(let i=0;i<300;i++)state.upsert({operationId:`done-${i}`,revision:1,state:'Confirmed'});
 assert.ok(state.operations.size<=256);assert.ok(state.operations.has('pending'));
});

const show='JoJo\'s Bizarre Adventure',first='STEEL BALL RUN JoJo\'s Bizarre Adventure 1st STAGE',sequel='STEEL BALL RUN JoJo\'s Bizarre Adventure 2nd - 3rd STAGE';
const sbr=(extra)=>({provider:'AniList',targetTitle:show,statusOnly:false,meaningfulChange:false,...extra});
test('the 08:22 run names both STAGE entries and drops the sentence that repeats the heading',()=>{
 const p=buildManualMarkPresentation({state:'Confirmed',sourceTitle:show,reason:'Trackers updated.',outcomes:[
  sbr({targetId:'a',targetTitle:first,kind:'Confirmed',progress:1,status:'Completed',step:'progress',meaningfulChange:true}),
  sbr({targetId:'b',targetTitle:sequel,kind:'Confirmed',progress:1,status:'Watching',step:'progress',meaningfulChange:true})]});
 assert.deepEqual(p.lines.map(l=>[l.entry,l.text]),[[first,'AniList · Episode 1 · Completed'],[sequel,'AniList · Episode 1 · Watching']]);
 assert.equal(p.detail,'');assert.equal(p.tone,'success');assert.equal(p.icon,'check');
});
test('the 08:28 repeat run is neutral and still says which entry each line means',()=>{
 const p=buildManualMarkPresentation({state:'NoChange',sourceTitle:show,reason:'No tracker changes were needed.',outcomes:[
  sbr({targetId:'a',targetTitle:first,kind:'NoChange',step:'manual-forward-only'}),
  sbr({targetId:'b',targetTitle:sequel,kind:'NoChange',step:'manual-forward-only'})]});
 assert.equal(p.tone,'neutral');assert.equal(p.detail,'');
 assert.deepEqual(p.lines.map(l=>[l.entry,l.text]),[[first,'AniList · Already at or past this episode'],[sequel,'AniList · Already at or past this episode']]);
});
test('waiting and ended states keep their sentence; dismissed is neutral, not an error',()=>{
 assert.equal(buildManualMarkPresentation({state:'Pending'}).tone,'active');
 assert.match(buildManualMarkPresentation({state:'Pending'}).detail,/Confirm within two minutes/);
 const dismissed=buildManualMarkPresentation({state:'Dismissed',reason:'Trackers were not updated. Jellyfin stays marked watched.'});
 assert.equal(dismissed.tone,'neutral');assert.equal(dismissed.detail,'Trackers were not updated. Jellyfin stays marked watched.');
 assert.equal(buildManualMarkPresentation({state:'Failed',reason:'Not retried automatically.'}).detail,'Not retried automatically.');
 assert.equal(buildManualMarkPresentation({state:'Failed'}).tone,'error');
});
test('tracker results read as sentences, never raw outcome names',()=>{
 const lines=buildManualMarkPresentation({state:'Partial',outcomes:[
  {provider:'Mal',kind:'Confirmed',progress:5,status:'Watching',step:'progress'},
  {provider:'AniList',kind:'NoChange',progress:5,status:'Watching',step:'progress'},
  {provider:'Kitsu',kind:'NoChange',step:'manual-forward-only'},
  {provider:'Annict',kind:'Skipped',step:'rewatch-unsupported'},
  {provider:'Shikimori',kind:'Skipped',step:'rewatch-disabled'},
  {provider:'Simkl',kind:'Unresolved',step:'resolve'}]}).lines.map(l=>l.text);
 assert.deepEqual(lines,[
  'MyAnimeList · Episode 5 · Watching',
  'AniList · Already up to date · Episode 5',
  'Kitsu · Already at or past this episode',
  'Annict · Rewatching is not supported',
  'Shikimori · Completed, and rewatching is off',
  'Simkl · No matching entry found']);
 for(const line of lines)assert.doesNotMatch(line,/NoChange|Unresolved|\bepisodes\b/);
});
test('failed, unconfirmed and status-only results stay readable',()=>{
 const lines=buildManualMarkPresentation({state:'Partial',outcomes:[
  {provider:'AniList',kind:'Failed',step:'progress'},
  {provider:'Mal',kind:'Unconfirmed',progress:2,step:'progress'},
  {provider:'Annict',kind:'Confirmed',status:'Watching',statusOnly:true,step:'status'},
  {provider:'Kitsu',kind:'Skipped',step:'plan-to-watch-only'},
  {provider:'Other',kind:'Mystery'}]}).lines.map(l=>l.text);
 assert.deepEqual(lines,[
  'AniList · Update failed',
  'MyAnimeList · Update not confirmed · Episode 2',
  'Annict · Watching',
  'Kitsu · Not on Plan to Watch',
  'Tracker · Updated']);
 assert.deepEqual(buildManualMarkPresentation({state:'Pending'}).lines,[]);
});
