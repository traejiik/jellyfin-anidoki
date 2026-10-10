import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../../jellyfin-anidoki/Configuration/NotificationStateJs.js', import.meta.url),'utf8').catch(()=> '');
const { buildPresentation, createNotificationState, createVisibleTimer } = await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
const outcome = (provider='AniList',extra={}) => ({provider,targetId:'1',targetTitle:'Anime',kind:'Confirmed',progress:3,status:'Watching',statusOnly:false,meaningfulChange:true,...extra});
const event = (extra={}) => ({eventId:'event',operationId:'operation',createdUtc:new Date(1000).toISOString(),sourceTitle:'Anime',sourceEpisode:3,isMovie:false,outcomes:[outcome()],...extra});
test('confirmed episode and completion presentation',()=> {
 assert.match(buildPresentation(event()).heading,/Logged to AniList/);
 assert.equal(buildPresentation(event()).detail,'Episode 3');
 assert.match(buildPresentation(event({outcomes:[outcome('AniList',{status:'Completed'})]})).heading,/Completed on AniList/);
});
test('Annict status cannot manufacture an episode',()=> {
 const p=buildPresentation(event({outcomes:[outcome('Annict',{statusOnly:true,progress:null})]}));
 assert.equal(p.heading,'Status updated on Annict'); assert.doesNotMatch(p.detail,/Episode/);
});
test('partial and mapped progress retain confirmed provider details',()=> {
 const partial=buildPresentation(event({outcomes:[outcome(),outcome('Mal',{kind:'Unconfirmed',meaningfulChange:false,progress:null})]}));
 assert.match(partial.heading,/Updated on AniList/); assert.match(partial.warning,/MyAnimeList update could not be confirmed/);
 const mapped=buildPresentation(event({sourceEpisode:13,outcomes:[outcome('AniList',{progress:1,targetTitle:'Second season'})]}));
 assert.equal(mapped.heading,'Tracker update'); assert.match(mapped.details[0],/Second season.*Episode 1/);
});
test('no-change/skips alone never yield success and never count as failures',()=> {
 assert.equal(buildPresentation(event({outcomes:[outcome('Mal',{kind:'NoChange'})]})),null);
 assert.equal(buildPresentation(event({outcomes:[outcome(),outcome('Mal',{kind:'Skipped'})]})).warning,'');
});
test('titles remain literal text; movies and unknown totals avoid fabricated fractions',()=> {
 const p=buildPresentation(event({sourceTitle:'<img onerror=bad>',isMovie:true}));
 assert.equal(p.title,'<img onerror=bad>');assert.doesNotMatch(p.detail,/of|\//);
});
test('queue caps visible at two, pending at ten, deduplicates and expires',()=> {
 let now=1000;const state=createNotificationState(()=>now);
 for(let i=0;i<20;i++)state.accept(event({eventId:'e'+i,operationId:'o'+i}));
 assert.equal(state.visible.length,2);assert.equal(state.pending.length,10);
 assert.equal(state.accept(event({eventId:'e0',operationId:'o0'})),false);
 state.dismiss(state.visible[0].eventId);assert.equal(state.visible.length,2);
 now+=120001;state.dismiss(state.visible[0].eventId);assert.equal(state.pending.length,0);
});
test('disabled/reset clears queue and dedup, then restarts from returned cursor',()=> {
 const state=createNotificationState(()=>1000);state.receive({enabled:true,cursor:'old:1',reset:false,events:[event()]});
 state.receive({enabled:false,cursor:'old:2',reset:false,events:[event()]});assert.equal(state.visible.length,0);
 state.receive({enabled:true,cursor:'new:1',reset:true,events:[]});assert.equal(state.cursor,'new:1');assert.equal(state.accept(event()),true);
});
test('visible timer pauses independently on hover, focus and hidden time without restarting',()=> {
 let now=0;const timer=createVisibleTimer(()=>now);now=2000;timer.pause('hover');now=4000;timer.pause('focus');timer.resume('hover');now=8000;
 assert.equal(timer.remaining(),4000);timer.resume('focus');now=10000;assert.equal(timer.remaining(),2000);
 timer.pause('hidden');now=30000;assert.equal(timer.remaining(),2000);timer.resume('hidden');now=32000;assert.equal(timer.remaining(),0);
});
test('different confirmed statuses preserve per-provider details at identical progress',()=> {
 const p=buildPresentation(event({outcomes:[outcome(),outcome('Mal',{status:'Completed'})]}));
 assert.equal(p.heading,'Tracker update');assert.match(p.details[0],/AniList.*Watching/);assert.match(p.details[1],/MyAnimeList.*Completed/);
});
test('Annict translated title is visible and movies never gain episode copy',()=> {
 const mapped=buildPresentation(event({outcomes:[outcome('Annict',{statusOnly:true,progress:null,targetTitle:'Second season'})]}));
 assert.equal(mapped.heading,'Tracker update');assert.match(mapped.details[0],/Second season.*Watching/);
 const movie=buildPresentation(event({isMovie:true,sourceEpisode:null,outcomes:[outcome('AniList',{progress:1,status:'Completed'})]}));
 assert.equal(movie.heading,'Completed on AniList');assert.equal(movie.detail,'Completed');assert.doesNotMatch(JSON.stringify(movie),/Episode/);
});
test('summarized aggregates explicitly disclose omitted tracker results',()=> {
 assert.match(buildPresentation(event({summarized:true})).details.at(-1),/Additional tracker results omitted/);
});
