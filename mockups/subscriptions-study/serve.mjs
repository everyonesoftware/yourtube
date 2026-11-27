import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';

const host = process.argv[2] || '127.0.0.1';
const file = new URL('./index.html', import.meta.url);
const server = createServer(async (req, res) => {
  if (req.url !== '/' && req.url !== '/index.html') return res.writeHead(404).end();
  try {
    const html = await readFile(file);
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' }).end(html);
  } catch {
    res.writeHead(500).end('Build the standalone study first.');
  }
});
server.listen(0, host, () => console.log(`http://${host}:${server.address().port}/`));
