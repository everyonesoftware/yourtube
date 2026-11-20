// Match the sage (#c0d7ae) video camera shown in the native title bar.
// Geometry is explicit in a 24x24 viewport: rounded body and right-hand lens.
// This is a limited geometry renderer, NOT a general SVG renderer.
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const root = fileURLToPath(new URL('../', import.meta.url));
const args = process.argv.slice(2);
if (args.length && (args.length !== 2 || args[0] !== '--output-directory' || !args[1])) {
  throw new Error('Usage: node scripts/generate-windows-icons.mjs [--output-directory PATH]');
}
const output = args.length ? resolve(args[1]) : resolve(root, 'apps/windows/YourTube.App/Assets');
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const ink = [192, 215, 174];
const halfStroke = 0.75;
const lens = [[15, 10], [21, 6.75], [21, 17.25], [15, 14]];
const samples = 8;

function onCamera(x, y) {
  // Signed distance to a rounded rectangle centered at (9, 12).
  const qx = Math.abs(x - 9) - 4, qy = Math.abs(y - 12) - 5;
  const distance = Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - 2;
  if (Math.abs(distance) <= halfStroke) return true;
  for (let index = 1; index < lens.length; index++) {
    const [ax, ay] = lens[index - 1], [bx, by] = lens[index];
    const dx = bx - ax, dy = by - ay;
    const t = Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)));
    if ((x - ax - t * dx) ** 2 + (y - ay - t * dy) ** 2 <= halfStroke ** 2) return true;
  }
  return false;
}
function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const bytes = Buffer.alloc(data.length + 12);
  bytes.writeUInt32BE(data.length);
  bytes.write(type, 4);
  data.copy(bytes, 8);
  bytes.writeUInt32BE(crc32(bytes.subarray(4, -4)), bytes.length - 4);
  return bytes;
}
function png(size) {
  const stride = 1 + size * 4;
  const raw = Buffer.alloc(size * stride);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    let covered = 0;
    for (let sy = 0; sy < samples; sy++) for (let sx = 0; sx < samples; sx++) {
      if (onCamera((x + (sx + 0.5) / samples) * 24 / size, (y + (sy + 0.5) / samples) * 24 / size)) covered++;
    }
    const offset = y * stride + 1 + x * 4;
    // PNG stores straight alpha: keep covered RGB sage even at the faintest
    // edge, avoiding dark halos when Windows composites onto any background.
    if (covered) {
      raw.set(ink, offset);
      raw[offset + 3] = Math.round(covered * 255 / (samples * samples));
    }
  }
  const header = Buffer.alloc(13);
  header.writeUInt32BE(size);
  header.writeUInt32BE(size, 4);
  header[8] = 8;
  header[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', header), chunk('IDAT', deflateSync(raw, { level: 9 })), chunk('IEND', Buffer.alloc(0)),
  ]);
}
const images = sizes.map(png);
const directory = Buffer.alloc(6 + sizes.length * 16);
directory.writeUInt16LE(1, 2);
directory.writeUInt16LE(sizes.length, 4);
let position = directory.length;
for (const [index, size] of sizes.entries()) {
  const offset = 6 + index * 16;
  directory[offset] = directory[offset + 1] = size % 256;
  directory.writeUInt16LE(1, offset + 4);
  directory.writeUInt16LE(32, offset + 6);
  directory.writeUInt32LE(images[index].length, offset + 8);
  directory.writeUInt32LE(position, offset + 12);
  position += images[index].length;
}
mkdirSync(output, { recursive: true });
writeFileSync(resolve(output, 'YourTube.ico'), Buffer.concat([directory, ...images]));
console.log(`Generated YourTube sage camera ICO (${sizes.join(', ')}px) in ${output}`);
