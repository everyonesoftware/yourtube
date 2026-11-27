import http from 'node:http';
import fs from 'node:fs';
const file=new URL('./index.html',import.meta.url);
const server=http.createServer((req,res)=>{const path=new URL(req.url,'http://localhost').pathname;if(!['/','/index.html'].includes(path)){res.writeHead(404);res.end('Not found');return;}res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'});fs.createReadStream(file).pipe(res);});
server.listen(0,'0.0.0.0',()=>console.log('YourTube player study port: '+server.address().port));
