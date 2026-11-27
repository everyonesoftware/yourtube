import {mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {execFileSync} from 'node:child_process';
import {groups,videos} from './reference.mjs';
const file=name=>new URL(name,import.meta.url);
const thumb=i=>file(`art/${i}.jpg`);
if(process.argv[2]){
  mkdirSync(file('art/'),{recursive:true});
  let i=0;
  for(const {file:source,x,y,w,h} of groups)for(const left of x){
    execFileSync('ffmpeg',['-v','error','-y','-i',join(process.argv[2],source),'-vf',`crop=${w}:${h}:${left}:${y},scale=360:202`,'-frames:v','1','-q:v','4','-threads','1',fileURLToPath(thumb(i++))]);
  }
}
const images=videos.map(v=>'data:image/jpeg;base64,'+readFileSync(thumb(v.id)).toString('base64'));
writeFileSync(file('index.html'),readFileSync(file('study.html'),'utf8')
  .replace('/* EMBEDDED IMAGES */ []',()=>JSON.stringify(images))
  .replace('/* REFERENCE VIDEOS */ []',()=>JSON.stringify(videos))
  .replace('/* HEADER STYLES */',()=>readFileSync(file('header.css'),'utf8'))
  .replace('/* SCOPED SEARCH */',()=>readFileSync(file('scope.js'),'utf8')));
