import test from 'node:test';import assert from 'node:assert/strict';
import { createDiscordDraft, discordCommand, discordDirty } from '../../../jellyfin-anidoki/Configuration/DiscordSettingsJs.js';
test('blank input keeps the saved webhook without becoming dirty',()=>{const saved={enabled:false,hasWebhook:true,revision:3};const draft=createDiscordDraft(saved);assert.equal(discordCommand(draft,saved).webhookAction,'Keep');assert.equal(discordDirty(draft,saved),false);});
test('replacement and explicit removal remain separate actions',()=>{const saved={enabled:true,hasWebhook:true,revision:3};const draft=createDiscordDraft(saved);draft.webhook='https://discord.com/example';assert.equal(discordCommand(draft,saved).webhookAction,'Replace');draft.clear=true;assert.equal(discordCommand(draft,saved).webhookAction,'Clear');assert.equal(discordCommand(draft,saved).enabled,false);});

import { secretInput } from '../../../jellyfin-anidoki/Configuration/CommonJs.js';
import mountDiscord from '../../../jellyfin-anidoki/Configuration/DiscordSettingsJs.js';
class Element extends EventTarget {
    constructor(tag='div',text='') { super(); this.tagName=tag; this.textContent=text; this.children=[]; }
    append(...elements) { this.children.push(...elements); for(const e of elements)e.parent=this; }
    replaceChildren(...elements) { this.children=[];this.append(...elements); }
    setAttribute(name,value) { (this.attributes ??= new Map()).set(name,String(value)); }
    getAttribute(name) { return this.attributes?.get(name) ?? null; }
    remove() { if(this.parent)this.parent.children=this.parent.children.filter(e=>e!==this); }
}
const flatten = node => [node,...node.children.flatMap(flatten)];
const flush = () => new Promise(resolve=>setImmediate(resolve));
function fixture(preview) {
    const ids=['discordEnabled','discordWebhook','discordWebhookControl','discordHelp','discordDetail','clearDiscord','testDiscord','discordStatus','discordPreview','discordPreviewToggle'];
    const nodes=Object.fromEntries(ids.map(id=>[id,new Element()]));
    nodes.discordWebhook.type='password';nodes.discordWebhookControl.append(nodes.discordWebhook);
    globalThis.document={createElement:tag=>new Element(tag),createElementNS:(_,tag)=>new Element(tag)};
    const root={querySelector:selector=>nodes[selector.slice(1)]};const calls=[];
    const common={secretInput,json:async(path,options)=>{calls.push({path,options});return path.endsWith('/settings')?{enabled:false,hasWebhook:false,revision:0,publicAddressReady:true}:preview();},
        request:()=>{throw new Error('No poster in this fixture');},element:(tag,css,text)=>new Element(tag,text),status:(node,text)=>node.textContent=text,errorMessage:async error=>error.message,assetUrl:name=>`AniDoki/assets/v/test/${name}`};
    globalThis.ApiClient={getUrl:path=>path};return {nodes,root,common,calls};
}
test('Preview uses only reads and renders the bold linked show with required episode details',async()=>{
    const env=fixture(()=>({showTitle:'Literal <title>',showUrl:'https://jf.example/web/#/details?id=show',episodeLine:'Episode 3 · Episode title',serverName:'Living room',footerIconUrl:'https://jf.example/logo.png',timestamp:'2026-10-10T16:00:00Z',fields:[{name:'AniList',value:'Updated progress to 3'}]}));
    const scope=new AbortController();await mountDiscord(env.root,env.common,{},scope.signal,()=>false);
    assert.equal(env.nodes.discordPreviewToggle.open,undefined);
    env.nodes.discordPreviewToggle.open=true;env.nodes.discordPreviewToggle.dispatchEvent(new Event('toggle'));await flush();
    assert.deepEqual(env.calls.map(c=>c.path),['AniDoki/user/discord/settings','AniDoki/user/discord/preview']);
    assert.ok(env.calls.every(c=>!c.options?.method || c.options.method==='GET'));
    const strong=flatten(env.nodes.discordPreview).find(n=>n.tagName==='strong'&&n.children.length);
    assert.equal(strong.children[0].tagName,'a');assert.equal(strong.children[0].textContent,'Literal <title>');
    assert.ok(flatten(env.nodes.discordPreview).some(n=>n.textContent==='Episode 3 · Episode title'));
    assert.ok(flatten(env.nodes.discordPreview).some(n=>n.textContent==='Updated progress to 3'));
    assert.ok(flatten(env.nodes.discordPreview).some(n=>n.textContent?.startsWith('Living room · Today at')));
    assert.ok(flatten(env.nodes.discordPreview).some(n=>n.tagName==='img'&&n.alt==='Jellyfin'));scope.abort();
});
test('An older Preview request cannot overwrite a newly opened preview',async()=>{
    const resolvers=[];const env=fixture(()=>new Promise(resolve=>resolvers.push(resolve)));
    const scope=new AbortController();await mountDiscord(env.root,env.common,{},scope.signal,()=>false);
    const toggle=env.nodes.discordPreviewToggle;
    toggle.open=true;toggle.dispatchEvent(new Event('toggle'));
    toggle.open=false;toggle.dispatchEvent(new Event('toggle'));
    toggle.open=true;toggle.dispatchEvent(new Event('toggle'));
    resolvers[1]({empty:true,message:'Current preview'});await flush();
    resolvers[0]({empty:true,message:'Stale preview'});await flush();
    assert.ok(flatten(env.nodes.discordPreview).some(n=>n.textContent==='Current preview'));
    assert.ok(!flatten(env.nodes.discordPreview).some(n=>n.textContent==='Stale preview'));scope.abort();
});

test('webhook reveal preserves a pasted draft without sending a request', async () => {
    const env=fixture(()=>({empty:true}));const scope=new AbortController();
    await mountDiscord(env.root,env.common,{},scope.signal,()=>false);
    const input=env.nodes.discordWebhook;
    assert.equal(input.parent.className,'doki-secret-input');
    input.value='https://discord.com/api/webhooks/123456789012345678/visual-test-only';
    input.dispatchEvent(new Event('input'));
    const reveal=input.parent.children[1];reveal.dispatchEvent(new Event('click'));
    assert.equal(input.type,'text');assert.ok(input.value.endsWith('/visual-test-only'));
    assert.equal(reveal.getAttribute('aria-pressed'),'true');
    reveal.dispatchEvent(new Event('click'));assert.equal(input.type,'password');
    assert.equal(env.calls.length,1);scope.abort();
});

test('editing a webhook does not rewrite its value and reset native editing state', async () => {
    const env=fixture(()=>({empty:true}));const scope=new AbortController();
    await mountDiscord(env.root,env.common,{},scope.signal,()=>false);
    const input=env.nodes.discordWebhook;let value=input.value,writes=0;
    Object.defineProperty(input,'value',{get:()=>value,set:next=>{value=next;writes++;},configurable:true});
    value='pasted draft';input.dispatchEvent(new Event('input'));
    assert.equal(writes,0);assert.equal(input.value,'pasted draft');scope.abort();
});

test('the Discord detail shows only while the switch is on, and turning it off drops a webhook draft', async () => {
    const env=fixture(()=>({empty:true}));const scope=new AbortController();const cache={};let changes=0;
    await mountDiscord(env.root,env.common,cache,scope.signal,()=>false,()=>changes++);
    assert.equal(env.nodes.discordDetail.hidden,true);
    env.nodes.discordEnabled.checked=true;env.nodes.discordEnabled.dispatchEvent(new Event('change'));
    assert.equal(env.nodes.discordDetail.hidden,false);
    env.nodes.discordWebhook.value='https://discord.com/api/webhooks/1/draft';env.nodes.discordWebhook.dispatchEvent(new Event('input'));
    env.nodes.discordEnabled.checked=false;env.nodes.discordEnabled.dispatchEvent(new Event('change'));
    assert.equal(env.nodes.discordDetail.hidden,true);assert.equal(cache.discordDraft.webhook,'');
    assert.equal(cache.discordDirty(),false);assert.equal(changes,3);scope.abort();
});

test('the page save sends the Discord command once and leaves nothing dirty', async () => {
    const env=fixture(()=>({empty:true}));const scope=new AbortController();const cache={};
    await mountDiscord(env.root,env.common,cache,scope.signal,()=>false);
    env.nodes.discordEnabled.checked=true;env.nodes.discordEnabled.dispatchEvent(new Event('change'));
    env.nodes.discordWebhook.value='https://discord.com/api/webhooks/1/new';env.nodes.discordWebhook.dispatchEvent(new Event('input'));
    env.common.json=async(path,options)=>{env.calls.push({path,options});return {enabled:true,hasWebhook:true,revision:1,publicAddressReady:true};};
    await cache.discordSave(scope.signal);
    const put=env.calls.filter(c=>c.options?.method==='PUT');
    assert.equal(put.length,1);assert.equal(put[0].options.body.webhookAction,'Replace');
    assert.equal(cache.discordDirty(),false);assert.equal(env.nodes.discordDetail.hidden,false);
    await cache.discordSave(scope.signal);assert.equal(env.calls.filter(c=>c.options?.method==='PUT').length,1);scope.abort();
});

test('the page discard restores the saved Discord state', async () => {
    const env=fixture(()=>({empty:true}));const scope=new AbortController();const cache={};
    await mountDiscord(env.root,env.common,cache,scope.signal,()=>false);
    env.nodes.discordEnabled.checked=true;env.nodes.discordEnabled.dispatchEvent(new Event('change'));
    assert.equal(cache.discordDirty(),true);
    cache.discordDiscard();
    assert.equal(cache.discordDirty(),false);assert.equal(env.nodes.discordEnabled.checked,false);assert.equal(env.nodes.discordDetail.hidden,true);scope.abort();
});
