import http from 'node:http';
import fs from 'node:fs';
const file=new URL('./yourtube-concept.html',import.meta.url);
http.createServer((req,res)=>{const path=new URL(req.url,'http://localhost').pathname;if(path!=='/'&&path!=='/yourtube-concept.html'){res.writeHead(404);res.end('Not found');return;}res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store'});fs.createReadStream(file).pipe(res)}).listen(8841,'0.0.0.0',()=>console.log('YourTube mockup on port 8841'));
