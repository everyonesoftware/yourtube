import assert from 'node:assert/strict';
import { existsSync, readFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { inflateSync } from 'node:zlib';
import test from 'node:test';

const root = fileURLToPath(new URL('../', import.meta.url));
const asset = join(root, 'apps/windows/YourTube.App/Assets/YourTube.ico');
const generator = join(root, 'scripts/generate-windows-icons.mjs');
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const ink = [192, 215, 174];

// Independent bit-at-a-time CRC implementation: do not trust encoder output.
function crc32(bytes) {
  let value = 0xffffffff;
  for (const byte of bytes) {
    value ^= byte;
    for (let bit = 0; bit < 8; bit++) value = value & 1 ? (value >>> 1) ^ 0xedb88320 : value >>> 1;
  }
  return (value ^ 0xffffffff) >>> 0;
}
function decodePng(bytes, size) {
  assert.deepEqual([...bytes.subarray(0, 8)], [137, 80, 78, 71, 13, 10, 26, 10]);
  let offset = 8;
  const chunks = [];
  const data = [];
  while (offset < bytes.length) {
    assert.ok(offset + 12 <= bytes.length, 'complete PNG chunk');
    const length = bytes.readUInt32BE(offset);
    const end = offset + 12 + length;
    assert.ok(end <= bytes.length, 'bounded PNG chunk');
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const payload = bytes.subarray(offset + 8, end - 4);
    assert.equal(bytes.readUInt32BE(end - 4), crc32(bytes.subarray(offset + 4, end - 4)), `${type} CRC`);
    chunks.push(type);
    if (type === 'IHDR') {
      assert.equal(length, 13);
      assert.equal(payload.readUInt32BE(), size);
      assert.equal(payload.readUInt32BE(4), size);
      assert.deepEqual([...payload.subarray(8)], [8, 6, 0, 0, 0], '8-bit noninterlaced RGBA');
    }
    if (type === 'IDAT') data.push(payload);
    if (type === 'IEND') assert.equal(length, 0);
    offset = end;
  }
  assert.deepEqual(chunks, ['IHDR', 'IDAT', 'IEND']);
  const rows = inflateSync(Buffer.concat(data));
  assert.equal(rows.length, size * (size * 4 + 1));
  const rgba = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) {
    assert.equal(rows[y * (size * 4 + 1)], 0, 'explicit unfiltered scanline');
    rows.copy(rgba, y * size * 4, y * (size * 4 + 1) + 1, (y + 1) * (size * 4 + 1));
  }
  return rgba;
}
function decodeIco(bytes) {
  assert.deepEqual([...bytes.subarray(0, 6)], [0, 0, 1, 0, sizes.length, 0]);
  let expectedOffset = 6 + sizes.length * 16;
  return sizes.map((size, index) => {
    const entry = 6 + index * 16;
    assert.equal(bytes[entry] || 256, size);
    assert.equal(bytes[entry + 1] || 256, size);
    assert.equal(bytes.readUInt16LE(entry + 2), 0, 'no palette/reserved bytes');
    assert.equal(bytes.readUInt16LE(entry + 4), 1);
    assert.equal(bytes.readUInt16LE(entry + 6), 32);
    const length = bytes.readUInt32LE(entry + 8);
    const offset = bytes.readUInt32LE(entry + 12);
    assert.equal(offset, expectedOffset, 'contiguous nonoverlapping directory');
    assert.ok(length > 0 && offset + length <= bytes.length);
    expectedOffset += length;
    if (index === sizes.length - 1) assert.equal(expectedOffset, bytes.length, 'no trailing data');
    return { size, rgba: decodePng(bytes.subarray(offset, offset + length), size) };
  });
}

test('checked-in ICO fully decodes at every Windows scale with sage camera geometry', () => {
  assert.ok(existsSync(asset), 'YourTube camera ICO is checked in');
  for (const { size, rgba } of decodeIco(readFileSync(asset))) {
    let visible = 0, partial = 0, opaque = 0;
    for (let offset = 0; offset < rgba.length; offset += 4) {
      const alpha = rgba[offset + 3];
      if (!alpha) { assert.deepEqual([...rgba.subarray(offset, offset + 3)], [0, 0, 0]); continue; }
      visible++;
      const pixel = offset / 4, x = pixel % size, y = Math.floor(pixel / size);
      assert.ok((x + 1) * 24 / size > 2.25 && x * 24 / size < 21.75 &&
        (y + 1) * 24 / size > 4.25 && y * 24 / size < 19.75, 'visible pixels remain within camera bounds');
      if (alpha === 255) opaque++; else partial++;
      assert.deepEqual([...rgba.subarray(offset, offset + 3)], ink, 'straight sage RGB, including antialiased edges');
    }
    assert.ok(visible > size * size * 0.15 && visible < size * size * 0.5, `${size}px outline coverage`);
    assert.ok(partial > 0, 'antialiased outline edges');
    // A one-device-pixel outline can straddle pixel boundaries at tiny scales.
    if (size >= 32) assert.ok(opaque > 0, 'solid outline interior at larger scales');
    const alphaAt = (x, y) => rgba[(Math.floor(y * size / 24) * size + Math.floor(x * size / 24)) * 4 + 3];
    for (const [x, y] of [[0, 0], [23, 23], [8, 12], [9, 12], [19, 12], [12, 1], [12, 23]]) assert.equal(alphaAt(x, y), 0, `transparent ${x},${y} at ${size}px`);
    for (const [x, y] of [[3, 12], [15, 12], [21, 12], [9, 5], [9, 19]]) assert.ok(alphaAt(x, y) > 0, `video camera body or viewfinder ${x},${y} at ${size}px`);
  }
});

test('Node regeneration is deterministic and byte-identical to checked-in ICO', () => {
  assert.ok(existsSync(generator), 'dependency-free Node generator exists');
  const directory = mkdtempSync(join(process.env.TMPDIR || tmpdir(), 'yourtube-icons-'));
  try {
    for (let run = 0; run < 2; run++) {
      const result = spawnSync(process.execPath, [generator, '--output-directory', directory], { encoding: 'utf8', cwd: directory });
      assert.equal(result.status, 0, result.stderr);
      assert.deepEqual(readFileSync(join(directory, 'YourTube.ico')), readFileSync(asset));
      decodeIco(readFileSync(join(directory, 'YourTube.ico')));
    }
  } finally { rmSync(directory, { recursive: true, force: true }); }
});

test('independent decoder rejects bad chunk CRCs and malformed ICO directories', () => {
  const original = readFileSync(asset);
  const pngStart = original.readUInt32LE(18);
  let offset = pngStart + 8;
  for (const type of ['IHDR', 'IDAT', 'IEND']) {
    const damaged = Buffer.from(original);
    const length = original.readUInt32BE(offset);
    assert.equal(original.toString('ascii', offset + 4, offset + 8), type);
    damaged[offset + 8 + length] ^= 1;
    assert.throws(() => decodeIco(damaged), new RegExp(`${type} CRC`));
    offset += length + 12;
  }
  for (const [position, value] of [[6, 17], [12, 24], [18, pngStart + 1], [14, original.length]]) {
    const damaged = Buffer.from(original);
    damaged.writeUInt32LE(value, position);
    assert.throws(() => decodeIco(damaged));
  }
  assert.throws(() => decodeIco(original.subarray(0, original.length - 1)));
  assert.throws(() => decodeIco(Buffer.concat([original, Buffer.from([0])])));
});

test('generator rejects unsupported CLI arguments rather than writing a guessed target', () => {
  for (const args of [['--unknown'], ['--output-directory'], ['--output-directory', ''], ['--output-directory', '.', 'extra']]) {
    const result = spawnSync(process.execPath, [generator, ...args], { encoding: 'utf8' });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Usage:/);
  }
});
