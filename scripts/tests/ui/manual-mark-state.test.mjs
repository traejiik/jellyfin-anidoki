import test from 'node:test';
import assert from 'node:assert/strict';
import { createManualMarkState, buildManualMarkPresentation } from '../../../jellyfin-anidoki/Configuration/ManualMarkStateJs.js';
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
