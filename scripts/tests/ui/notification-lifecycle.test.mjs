import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const stateSource = await readFile(new URL('../../../jellyfin-anidoki/Configuration/NotificationStateJs.js',import.meta.url),'utf8');
const stateUrl='data:text/javascript;base64,'+Buffer.from(stateSource).toString('base64');
const source=(await readFile(new URL('../../../jellyfin-anidoki/Configuration/NotificationsJs.js',import.meta.url),'utf8')).replace("'./notification-state.js'",JSON.stringify(stateUrl));
const {startNotifications}=await import('data:text/javascript;base64,'+Buffer.from(source).toString('base64'));
class Element extends EventTarget {
 constructor(tag){super();this.tagName=tag.toUpperCase();this.children=[];this.dataset={};this.style={};this.isConnected=true;}
 append(...items){for(const item of items){if(!item)continue;item.remove();item.parent=this;this.children.push(item);}}
 remove(){if(this.parent)this.parent.children=this.parent.children.filter(x=>x!==this);this.parent=null;}
 setAttribute(name,value){this[name]=value;}
 contains(element){return this===element||this.children.some(x=>x.contains(element));}
}
function setup(ajax){
 const doc=new EventTarget();doc.head=new Element('head');doc.body=new Element('body');doc.activeElement=null;doc.hidden=false;doc.fullscreenElement=null;doc.createElement=tag=>new Element(tag);
 doc.querySelector=()=>null;doc.querySelectorAll=()=>[];
 const win=new EventTarget();win.timeouts=new Map();win.intervals=new Map();let serial=0;
 win.setTimeout=(fn,delay)=>{const id=++serial;win.timeouts.set(id,{fn,delay});return id;};win.clearTimeout=id=>win.timeouts.delete(id);
 win.setInterval=(fn,delay)=>{const id=++serial;win.intervals.set(id,{fn,delay});return id;};win.clearInterval=id=>win.intervals.delete(id);
 win.ApiClient={isLoggedIn:()=>true,serverAddress:()=>'/jellyfin',getCurrentUserId:()=> 'user',deviceId:()=> 'device',accessToken:()=> 'token',setRequestHeaders:headers=>{headers.Authorization='fixture';},getUrl:(path,params)=> '/jellyfin/'+path+(params?.cursor?'?cursor='+params.cursor:''),ajax};
 win.fetch=async(url,args)=> { const result=await ajax({...args,url});return {ok:true,json:async()=>result}; };
 const tick=async()=>{const first=[...win.timeouts].sort((a,b)=>a[1].delay-b[1].delay)[0];if(first){win.timeouts.delete(first[0]);await first[1].fn();}await new Promise(resolve=>setImmediate(resolve));};
 return {win,doc,tick};
}
const response=(extra={})=>({enabled:true,cursor:'epoch:0',reset:false,events:[],...extra});
const event={eventId:'one',operationId:'one',createdUtc:new Date().toISOString(),sourceTitle:'Literal <script>',sourceEpisode:3,outcomes:[{provider:'AniList',targetId:'1',targetTitle:'Literal <script>',kind:'Confirmed',progress:3,status:'Watching',meaningfulChange:true}]};
test('one request across repeated navigation; disposal aborts request and all timers',async()=>{
 let requests=0,signal,resolve;const env=setup(args=>{requests++;signal=args.signal;return new Promise(r=>resolve=r);});
 const runtime=startNotifications(env.win,env.doc);env.doc.dispatchEvent(new Event('viewshow'));await env.tick();assert.equal(requests,1);
 runtime.dispose();assert.equal(signal.aborted,true);assert.equal(env.win.timeouts.size,0);resolve(response());await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));
 assert.equal(env.win.timeouts.size,0);assert.equal(env.win.intervals.size,0);assert.equal(env.doc.body.children.length,0);
});
test('ordinary polls retain cursor; reset/disabled remove owned toasts',async()=>{
 const urls=[];let calls=0;const env=setup(async args=>{urls.push(args.url);calls++;return response(calls===2?{cursor:'epoch:1',events:[event]}:calls===3?{enabled:false,cursor:'epoch:2'}:{});});
 const runtime=startNotifications(env.win,env.doc);await new Promise(resolve=>setImmediate(resolve));await env.tick();assert.match(urls[1],/cursor=epoch:0/);
 assert.equal(env.doc.body.children.length,1);await env.tick();assert.equal(env.doc.body.children.length,0);assert.equal([...env.win.timeouts.values()][0].delay,30000);runtime.dispose();
});
test('fullscreen mounts rich overlay in container and suppresses video-only overlay',async()=>{
 let calls=0;const env=setup(async()=>response({events:++calls===1?[]:[event]}));const runtime=startNotifications(env.win,env.doc);await new Promise(resolve=>setImmediate(resolve));await env.tick();
 const full=new Element('section');env.doc.fullscreenElement=full;env.doc.dispatchEvent(new Event('fullscreenchange'));assert.equal(full.children.length,1);
 env.doc.fullscreenElement=new Element('video');env.doc.dispatchEvent(new Event('fullscreenchange'));assert.equal(full.children[0].hidden,true);
 env.doc.fullscreenElement=null;env.doc.dispatchEvent(new Event('fullscreenchange'));assert.equal(env.doc.body.children.length,1);runtime.dispose();
});
test('logout clears visible UI immediately on navigation; stale response cannot render',async()=>{
 let calls=0;const env=setup(async()=>response({events:++calls===1?[]:[event]}));const runtime=startNotifications(env.win,env.doc);await new Promise(resolve=>setImmediate(resolve));await env.tick();
 assert.equal(env.doc.body.children.length,1);env.win.ApiClient.isLoggedIn=()=>false;env.doc.dispatchEvent(new Event('viewshow'));assert.equal(env.doc.body.children.length,0);runtime.dispose();
});
test('missing plugin tears down while transient transport backs off without writes',async()=>{
 const missing=setup(async()=>{throw {status:404};});startNotifications(missing.win,missing.doc);await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));assert.equal(missing.win.timeouts.size,0);
 const transient=setup(async()=>{throw {status:500};});const runtime=startNotifications(transient.win,transient.doc);await new Promise(resolve=>setImmediate(resolve));await new Promise(resolve=>setImmediate(resolve));assert.equal([...transient.win.timeouts.values()][0].delay,5000);await transient.tick();assert.equal([...transient.win.timeouts.values()][0].delay,10000);runtime.dispose();
});
test('pagehide stops scheduling until pageshow and reinitialization disposes prior runtime',async()=>{
 const env=setup(async()=>response());const first=startNotifications(env.win,env.doc);await new Promise(resolve=>setImmediate(resolve));env.win.dispatchEvent(new Event('pagehide'));assert.equal(env.win.timeouts.size,0);
 env.win.dispatchEvent(new Event('pageshow'));assert.equal(env.win.timeouts.size,1);const next=startNotifications(env.win,env.doc);assert.notEqual(first,next);await new Promise(resolve=>setImmediate(resolve));next.dispose();assert.equal(env.win.timeouts.size,0);
});
test('navigation before ApiClient readiness cannot cancel style bootstrap',async()=>{
 let calls=0;const env=setup(async()=>{calls++;return response();});const api=env.win.ApiClient;delete env.win.ApiClient;
 const runtime=startNotifications(env.win,env.doc);env.doc.dispatchEvent(new Event('viewshow'));await env.tick();assert.equal(calls,0);
 env.win.ApiClient=api;await env.tick();assert.equal(env.doc.head.children.length,1);assert.equal(calls,1);runtime.dispose();
});
test('pagehide during bootstrap resumes installing styles on pageshow',async()=>{
 const env=setup(async()=>response());const api=env.win.ApiClient;delete env.win.ApiClient;const runtime=startNotifications(env.win,env.doc);
 env.win.dispatchEvent(new Event('pagehide'));assert.equal(env.win.timeouts.size,0);env.win.ApiClient=api;
 env.win.dispatchEvent(new Event('pageshow'));await new Promise(resolve=>setImmediate(resolve));assert.equal(env.doc.head.children.length,1);runtime.dispose();
});
test('authorization rejection after visible toast stops render interval and same-identity polling',async()=>{
 let calls=0;const env=setup(async()=>{if(++calls===3)throw {status:403};return response({events:calls===2?[event]:[]});});
 const runtime=startNotifications(env.win,env.doc);await new Promise(resolve=>setImmediate(resolve));await env.tick();assert.equal(env.win.intervals.size,1);
 await env.tick();assert.equal(env.win.intervals.size,0);assert.equal(env.doc.body.children.length,0);await env.tick();assert.equal(calls,3);runtime.dispose();
});
test('WebKit fullscreen uses prefixed container and pauses unsupported native video overlay',async()=>{
 let calls=0;const env=setup(async()=>response({events:++calls===1?[]:[event]}));const runtime=startNotifications(env.win,env.doc);await new Promise(resolve=>setImmediate(resolve));await env.tick();
 const full=new Element('section');env.doc.webkitFullscreenElement=full;env.doc.dispatchEvent(new Event('webkitfullscreenchange'));assert.equal(full.children.length,1);
 env.doc.dispatchEvent(new Event('webkitbeginfullscreen'));assert.equal(full.children[0].hidden,true);
 env.doc.dispatchEvent(new Event('webkitendfullscreen'));assert.equal(full.children[0].hidden,false);
 env.doc.webkitFullscreenElement=null;env.doc.dispatchEvent(new Event('webkitfullscreenchange'));assert.equal(env.doc.body.children.length,1);runtime.dispose();
});
