import {createServer} from 'node:http';
import {readFileSync, writeFileSync} from 'node:fs';

const html = readFileSync(new URL('./index.html', import.meta.url), 'utf8');
const artwork = readFileSync(new URL('../thumb-0.jpg', import.meta.url)).toString('base64');
const standalone = html.replace('../thumb-0.jpg', `data:image/jpeg;base64,${artwork}`);
const [host, output] = process.argv.slice(2);
if (output) writeFileSync(output, standalone);
if (host) createServer((req, res) => {
  if (new URL(req.url, 'http://preview.invalid').pathname !== '/') { res.writeHead(404).end(); return; }
  res.writeHead(200, {'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store'}).end(standalone);
}).listen(0, host, function () { console.log(`Listening on ${host}:${this.address().port}`); });
