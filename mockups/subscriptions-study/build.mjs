import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';

const screenshot = process.argv[2];
if (!screenshot) throw new Error('Usage: node build.mjs <reference-screenshot.png>');
const source = readFileSync(new URL('./study.html', import.meta.url), 'utf8');
const images = [248, 566].flatMap(y => [240, 650, 1060, 1470].map(x => {
  const jpg = execFileSync('ffmpeg', ['-v', 'error', '-threads', '1', '-i', screenshot, '-vf', `crop=395:223:${x}:${y}:exact=1`, '-frames:v', '1', '-threads', '1', '-c:v', 'mjpeg', '-q:v', '3', '-f', 'image2pipe', 'pipe:1']);
  if (!jpg.length) throw new Error(`No thumbnail for crop ${x},${y}`);
  return `data:image/jpeg;base64,${jpg.toString('base64')}`;
}));
writeFileSync(new URL('./index.html', import.meta.url), source.replace('/* ARTWORK */[]', JSON.stringify(images)));
