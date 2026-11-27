import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
const host=process.env.SEARCH_STUDY_BIND ?? '127.0.0.1';
const server=createServer(async(req,res)=>{
  if(req.url!=='/search-study' || !['GET','HEAD'].includes(req.method)){res.writeHead(404).end();return;}
  try{const body=await readFile(new URL('./index.html',import.meta.url));res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'}).end(req.method==='HEAD'?undefined:body);}catch{res.writeHead(500).end('Build the study first.');}
});
server.listen(Number(process.env.SEARCH_STUDY_PORT ?? 0),host,()=>console.log(`http://${host}:${server.address().port}/search-study`));
