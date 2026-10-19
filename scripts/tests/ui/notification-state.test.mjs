import test from 'node:test';
import assert from 'node:assert/strict';
import { importConfiguration } from './load-configuration.mjs';
const { buildPresentation, createNotificationState, createVisibleTimer } = await importConfiguration('NotificationStateJs.js');
const outcome = (provider='AniList',extra={}) => ({provider,targetId:'1',targetTitle:'Anime',kind:'Confirmed',progress:3,status:'Watching',statusOnly:false,meaningfulChange:true,...extra});
const event = (extra={}) => ({eventId:'event',operationId:'operation',createdUtc:new Date(1000).toISOString(),sourceTitle:'Anime',sourceEpisode:3,isMovie:false,outcomes:[outcome()],...extra});
const lines = p => p.lines.map(l => [l.entry, l.text]);
test('confirmed episode and completion presentation',()=> {
 const p=buildPresentation(event());
 assert.equal(p.heading,'Logged to AniList'); assert.equal(p.detail,'Episode 3'); assert.equal(p.tone,'success');
 assert.deepEqual(lines(p),[['','AniList · Episode 3 · Watching']],'a plain update still gets the results box');
 assert.match(buildPresentation(event({outcomes:[outcome('AniList',{status:'Completed'})]})).heading,/Completed on AniList/);
});
test('Annict status cannot manufacture an episode',()=> {
 const p=buildPresentation(event({outcomes:[outcome('Annict',{statusOnly:true,progress:null})]}));
 assert.equal(p.heading,'Status updated on Annict'); assert.equal(p.detail,'Episode 3','the top says what was watched');
 assert.deepEqual(lines(p),[['','Annict · Watching']]);
});
test('partial and mapped progress name the result and the entry it landed on',()=> {
 const partial=buildPresentation(event({outcomes:[outcome(),outcome('Mal',{kind:'Unconfirmed',meaningfulChange:false,progress:null})]}));
 assert.equal(partial.heading,'Logged to AniList');
 assert.deepEqual(lines(partial),[['','AniList · Episode 3 · Watching'],['','MyAnimeList · Update not confirmed']]);
 assert.match(partial.lines[1].note,/won't be retried/); assert.equal(partial.lines[1].tone,'attention');
 const mapped=buildPresentation(event({sourceEpisode:13,outcomes:[outcome('AniList',{progress:1,targetTitle:'Second season'})]}));
 assert.equal(mapped.heading,'Logged to AniList'); assert.equal(mapped.detail,'Episode 13');
 assert.deepEqual(lines(mapped),[['Second season','AniList · Episode 1 · Watching']]);
});
test('no-change/skips alone never yield success and never become lines',()=> {
 assert.equal(buildPresentation(event({outcomes:[outcome('Mal',{kind:'NoChange'})]})),null);
 assert.deepEqual(lines(buildPresentation(event({outcomes:[outcome(),outcome('Mal',{kind:'Skipped',step:'rewatch-disabled',meaningfulChange:false})]}))),[['','AniList · Episode 3 · Watching']]);
});
test('the Steel Ball Run sequel walk logs the 2nd - 3rd STAGE entry by name',()=> {
 const first='STEEL BALL RUN JoJo\'s Bizarre Adventure 1st STAGE', sequel='STEEL BALL RUN JoJo\'s Bizarre Adventure 2nd - 3rd STAGE';
 const p=buildPresentation(event({sourceTitle:'JoJo\'s Bizarre Adventure',outcomes:[
  outcome('AniList',{targetId:'a',targetTitle:first,kind:'Skipped',step:'rewatch-disabled',meaningfulChange:false,progress:null}),
  outcome('AniList',{targetId:'b',targetTitle:sequel,progress:2})]}));
 assert.equal(p.heading,'Logged to AniList'); assert.deepEqual(lines(p),[[sequel,'AniList · Episode 2 · Watching']]);
});
test('two trackers get one line each, and a repeated result for one entry collapses',()=> {
 const p=buildPresentation(event({outcomes:[outcome(),outcome('Mal'),outcome()]}));
 assert.equal(p.heading,'Logged to AniList and MyAnimeList');
 assert.deepEqual(lines(p),[['','MyAnimeList · Episode 3 · Watching'],['','AniList · Episode 3 · Watching']]);
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
test('different confirmed statuses keep a line per provider at identical progress',()=> {
 const p=buildPresentation(event({outcomes:[outcome(),outcome('Mal',{status:'Completed'})]}));
 assert.equal(p.heading,'Logged to AniList and MyAnimeList');
 assert.deepEqual(lines(p),[['','AniList · Episode 3 · Watching'],['','MyAnimeList · Episode 3 · Completed']]);
});
test('Annict translated title is visible and movies never gain episode copy',()=> {
 const mapped=buildPresentation(event({outcomes:[outcome('Annict',{statusOnly:true,progress:null,targetTitle:'Second season'})]}));
 assert.equal(mapped.heading,'Status updated on Annict');assert.deepEqual(lines(mapped),[['Second season','Annict · Watching']]);
 const movie=buildPresentation(event({isMovie:true,sourceEpisode:null,outcomes:[outcome('AniList',{progress:1,status:'Completed'})]}));
 assert.equal(movie.heading,'Completed on AniList');assert.equal(movie.detail,'');assert.deepEqual(lines(movie),[['','AniList · Completed']]);
 assert.doesNotMatch(JSON.stringify(movie),/Episode/);
});
test('summarized aggregates explicitly disclose omitted tracker results',()=> {
 assert.match(buildPresentation(event({summarized:true})).note,/Additional tracker results omitted/);
});
test('raw outcome names and enum provider names never reach the toast',()=> {
 const p=buildPresentation(event({outcomes:[outcome('Mal'),outcome('Kitsu',{kind:'Failed',meaningfulChange:false,progress:null})]}));
 assert.doesNotMatch(p.lines.map(l=>`${l.entry} ${l.text}`).join('\n'),/\bMal\b|Unconfirmed|NoChange/);
 assert.equal(p.lines[1].text,'Kitsu · Update failed'); assert.equal(p.lines[1].tone,'error'); assert.equal(p.lines[1].icon,'cross');
});
