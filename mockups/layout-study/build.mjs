import { readFileSync, writeFileSync } from 'node:fs';
const here = name => new URL(name, import.meta.url);
const art = [0,1,2,3,5,6,7].map((id,i)=>`--art${i}:url(data:image/jpeg;base64,${readFileSync(here(`../thumb-${id}.jpg`)).toString('base64')})`).join(';');
const css = readFileSync(here('study.css'),'utf8');
const script = readFileSync(here('study.js'),'utf8');
const { render, initial } = await import(here('study.js'));
writeFileSync(here('index.html'),`<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>YourTube — Layout study</title><style>:root{${art}}${css}</style></head><body>${render(initial)}<script type="module">${script}</script></body></html>`);
