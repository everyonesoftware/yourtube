import http from 'node:http';
import { createReadStream } from 'node:fs';
const file=new URL('index.html',import.meta.url);
http.createServer((req,res)=>{
 if(!['/','/index.html'].includes(new URL(req.url,'http://localhost').pathname)){res.writeHead(404).end();return;}
 res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'});
 createReadStream(file).pipe(res);
}).listen(8843,'0.0.0.0',()=>console.log('YourTube layout study on port 8843'));
