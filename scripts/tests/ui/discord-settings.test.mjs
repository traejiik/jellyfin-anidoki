import test from 'node:test';import assert from 'node:assert/strict';
import { createDiscordDraft, discordCommand, discordDirty } from '../../../jellyfin-anidoki/Configuration/DiscordSettingsJs.js';
test('blank input keeps the saved webhook without becoming dirty',()=>{const saved={enabled:false,hasWebhook:true,revision:3};const draft=createDiscordDraft(saved);assert.equal(discordCommand(draft,saved).webhookAction,'Keep');assert.equal(discordDirty(draft,saved),false);});
test('replacement and explicit removal remain separate actions',()=>{const saved={enabled:true,hasWebhook:true,revision:3};const draft=createDiscordDraft(saved);draft.webhook='https://discord.com/example';assert.equal(discordCommand(draft,saved).webhookAction,'Replace');draft.clear=true;assert.equal(discordCommand(draft,saved).webhookAction,'Clear');assert.equal(discordCommand(draft,saved).enabled,false);});

import mountDiscord from '../../../jellyfin-anidoki/Configuration/DiscordSettingsJs.js';
class Element extends EventTarget {
    constructor(tag='div',text='') { super(); this.tagName=tag; this.textContent=text; this.children=[]; }
    append(...elements) { this.children.push(...elements); for(const e of elements)e.parent=this; }
    replaceChildren(...elements) { this.children=[];this.append(...elements); }
    remove() { if(this.parent)this.parent.children=this.parent.children.filter(e=>e!==this); }
}
const flatten = node => [node,...node.children.flatMap(flatten)];
const flush = () => new Promise(resolve=>setImmediate(resolve));
function fixture(preview) {
    const ids=['discordEnabled','discordWebhook','discordHelp','saveDiscord','clearDiscord','testDiscord','discordStatus','discordPreview','discordPreviewToggle'];
    const nodes=Object.fromEntries(ids.map(id=>[id,new Element()]));
    const root={querySelector:selector=>nodes[selector.slice(1)]};const calls=[];
    const common={json:async(path,options)=>{calls.push({path,options});return path.endsWith('/settings')?{enabled:false,hasWebhook:false,revision:0,publicAddressReady:true}:preview();},
        request:()=>{throw new Error('No poster in this fixture');},element:(tag,css,text)=>new Element(tag,text),status:(node,text)=>node.textContent=text,errorMessage:async error=>error.message};
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
