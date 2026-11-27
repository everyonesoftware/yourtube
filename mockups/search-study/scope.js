const q=s=>document.querySelector(s), app=q('#app'), player=q('#player');
const ids=(start,end)=>dataset.slice(start,end).map(v=>v.id);
const pages={home:{title:'Home',label:'Search YouTube',scope:'YouTube',ids:ids(0,10)},subs:{title:'Subscriptions',label:'Search subscriptions',scope:'Subscriptions',ids:ids(10,18)},history:{title:'History',label:'Search history',scope:'History',ids:ids(18,26)}};
const channels=['All Subscriptions','PBS NewsHour','CBC News','NBC Sports'];
const queries={home:'',subs:'',history:''};
const extraIcons={refresh:'<path d="M20 10a8 8 0 1 0 0 5m0-11v6h-6"/>',settings:'<circle cx="12" cy="12" r="3"/><path d="m9 3-1 3-3 1-2 5 2 5 3 1 1 3h6l1-3 3-1 2-5-2-5-3-1-1-3z"/>'};
Object.assign(paths,extraIcons);
const hints={web:'Web · Enter to search',windows:'Windows · Ctrl+K focuses search',android:'Android · Tap a page to change scope',tv:'Android TV · Arrow keys to move, Enter to select, Escape to cancel editing'};
let platform='windows',route='home',channel=0,editing=false,draft='',playerReturn;
const focusSearch=()=> (q('#query')||q('#edit-query')).focus();
function matches(){
 const query=queries[route], ids=route==='home'&&query?dataset.map(v=>v.id):pages[route].ids;
 return ids.map(id=>dataset[id]).filter(v=>(route!=='subs'||!channel||v.channel===channels[channel])&&
   (!query||query.toLowerCase().split(/\s+/).every(w=>`${v.title} ${v.channel}`.toLowerCase().includes(w))));
}
function art(v){return `<span class="art"><img alt="" src="${images[v.id]}"></span>`;}
function card(v){return `<button class="result" data-video="${v.id}">${art(v)}<span><b>${v.title}</b><small>${v.channel}${v.age?' · '+v.age:''}</small></span></button>`;}
const listing=rows=>`<div class="listing">${rows.map(card).join('')}</div>`;
function keyboard(){return `<div class="tv-kbd" aria-label="On-screen keyboard"><output class="tv-value" aria-live="polite"></output><div class="keys">${'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'.split('').map(c=>`<button data-key="${c}">${c}</button>`).join('')}</div><div class="key-actions"><button class="chip" data-key="space">Space</button><button class="chip" data-key="delete">Delete</button><button class="chip" data-key="clear">Clear text</button><button class="primary" id="tv-submit">Search videos</button></div></div>`;}
function searchBox(){
 const label=pages[route].label, clear=`<button type="button" class="icon-btn" id="clear-query" aria-label="Clear search" ${queries[route]?'':'hidden'}>${icon('close')}</button>`;
 if(platform==='tv')return `<div class="searchbar"><button class="primary" id="edit-query" aria-label="Edit query">${icon('search')} ${label}<span id="tv-query"></span></button>${clear}</div>`;
 return `<form class="searchbar" role="search" aria-label="${label}"><div class="field">${icon('search')}<input id="query" type="text" enterkeyhint="search" placeholder="${label}" aria-label="${label}" autocomplete="off">${clear}</div><button class="primary" type="submit">Search</button></form>`;
}
function nav(className){return `<nav class="page-nav ${className}" aria-label="YourTube pages">${Object.entries(pages).map(([key,p])=>`<button data-page="${key}" ${route===key?'aria-current="page"':''}>${icon(key)} ${p.title}</button>`).join('')}${className==='nav'?`<span>${icon('settings')} Settings</span>`:''}</nav>`;}
function render(){
 const page=pages[route], query=queries[route], rows=matches(), scope=route==='subs'&&channel?channels[channel]:page.scope;
 const filters=`<aside class="channel-rail" aria-labelledby="channel-label"><h3 id="channel-label">Subscribed Channels</h3>${channels.map((name,i)=>`<button class="chip" data-channel="${i}" aria-pressed="${channel===i}">${name}</button>`).join('')}</aside>`;
 const results=route==='home'&&!query?['Recommended','Your home for Grand Theft Auto VI'].map((title,i)=>`<section class="feed-group shelf"><h3>${title}</h3>${listing(rows.slice(i*5,i*5+5))}</section>`).join(''):rows.length?listing(rows):`<section class="empty"><h3>No matches in ${scope}</h3><p>Try another query or clear search to see this page’s videos.</p></section>`;
 const content=`${query?`<div class="results-announcement" role="status"><strong id="summary"></strong><span>${rows.length} sample ${rows.length===1?'video':'videos'}</span></div>`:''}${results}`;
 app.className=`app ${platform}`;
 q('#guide').textContent='Try “Lumos” on all three pages to compare scopes.';
 q('#device-hint').textContent=hints[platform];
 for(const b of document.querySelectorAll('[data-platform]'))b.setAttribute('aria-pressed',String(b.dataset.platform===platform));
 app.innerHTML=`<header class="bar"><div class="brand"><span class="mark">${icon('play')}</span>YourTube</div>${platform==='web'?'<span class="web-address">yourtube.example</span>':''}${platform==='windows'?'<span class="chrome" aria-hidden="true">− □ ×</span>':''}</header>${nav('mobile-nav')}<div class="body">${nav('nav')}<section class="workspace" aria-labelledby="page-title"><header class="page-head"><h2 id="page-title">${page.title}</h2>${searchBox()}<button class="page-refresh icon-btn" id="refresh-page" aria-label="Refresh page" title="Refresh sample videos">${icon('refresh')}</button></header>${editing?keyboard():''}${route==='subs'?`<div class="browse-split">${filters}<div>${content}</div></div>`:content}</section></div>`;
 if(q('#query'))q('#query').value=query;
 if(q('#tv-query'))q('#tv-query').textContent=query?` · ${query.length>24?query.slice(0,23)+'…':query}`:'';
 if(q('.tv-value'))q('.tv-value').textContent=draft||'Enter a search';
 if(q('#summary'))q('#summary').textContent=`Results in ${scope} for “${query}”`;
}
function submit(value){queries[route]=value.trim().slice(0,160);editing=false;render();focusSearch();}
function preview(id){
 const v=dataset[id];playerReturn=document.activeElement;
 player.innerHTML=`<div class="close-row"><div class="source">Video preview</div><button class="icon-btn" id="close-player" aria-label="Close preview" autofocus>${icon('close')}</button></div>${art(v)}<h2 id="player-title">${v.title}</h2><p>${v.channel} · ${v.duration}</p><p>Playback is not connected in this mockup.</p>`;
 player.showModal();q('#close-player').onclick=()=>player.close();
}
player.addEventListener('close',()=>playerReturn?.focus());
document.querySelector('.controls').addEventListener('click',e=>{const b=e.target.closest('[data-platform]');if(!b)return;platform=b.dataset.platform;editing=false;render();});
app.addEventListener('submit',e=>{e.preventDefault();submit(q('#query').value);});
app.addEventListener('input',e=>{if(e.target.id==='query')q('#clear-query').hidden=!e.target.value&&!queries[route];});
app.addEventListener('click',e=>{
 const b=e.target.closest('button');if(!b)return;
 const {page,channel:choice,video,key}=b.dataset;
 if(page){const isMobile=b.closest('.mobile-nav');route=page;editing=false;render();q(`.${isMobile?'mobile-nav':'nav'} [data-page=${page}]`).focus();}
 else if(choice!==undefined){channel=Number(choice);render();q(`[data-channel=${JSON.stringify(choice)}]`).focus();}
 else if(video!==undefined)preview(Number(video));
 else if(key){draft=(key==='space'?draft+' ':key==='delete'?draft.slice(0,-1):key==='clear'?'':draft+key.toLowerCase()).slice(0,160);q('.tv-value').textContent=draft||'Enter a search';}
 else if(b.id==='tv-submit')submit(draft);
 else if(b.id==='clear-query')submit('');
 else if(b.id==='refresh-page'){editing=false;render();q('#refresh-page').focus();}
 else if(b.id==='edit-query'){draft=queries[route];editing=true;render();q('[data-key=A]').focus();}
});
document.addEventListener('keydown',e=>{
 if(player.open)return;
 if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){e.preventDefault();focusSearch();return;}
 if(e.key==='Escape'&&editing){e.preventDefault();editing=false;render();focusSearch();return;}
 if(platform!=='tv'||!app.contains(document.activeElement)||!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key))return;
 const active=document.activeElement, buttons=[...app.querySelectorAll('button')].filter(b=>b.offsetWidth), chars=[...app.querySelectorAll('.keys button')], i=chars.indexOf(active);
 const dx=e.key==='ArrowRight'?1:e.key==='ArrowLeft'?-1:0,dy=e.key==='ArrowDown'?1:e.key==='ArrowUp'?-1:0;
 if(i>=0){const next=dx?(Math.floor((i+dx)/6)===Math.floor(i/6)?chars[i+dx]:null):chars[i+dy*6]||(dy>0?q('[data-key=space]'):q('#edit-query'));next?.focus();e.preventDefault();return;}
 const center=b=>{const r=b.getBoundingClientRect();return [r.x+r.width/2,r.y+r.height/2];};
 const [x,y]=center(active);let best=null,score=Infinity;
 for(const b of buttons){if(b===active)continue;const [bx,by]=center(b),forward=dx?(bx-x)*dx:(by-y)*dy,cross=Math.abs(dx?by-y:bx-x),rank=forward+2.4*cross;if(forward>6&&rank<score){best=b;score=rank;}}
 if(best){e.preventDefault();best.focus();}
});
render();
