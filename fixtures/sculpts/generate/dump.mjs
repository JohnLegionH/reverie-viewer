// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

// Oracle dump: the web viewer's sculpt.ts (web-viewer HEAD, client/src/mesh/sculpt.ts) over generated maps.
import { writeFileSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
globalThis.ImageData = class { constructor(w, h) { this.width = w; this.height = h; this.data = new Uint8ClampedArray(w * h * 4); } };
const { buildSculptGeometry } = await import('./sculpt.ts');

// Generated maps (the same formulas as SculptZoo in the repo, but the oracle stores the bytes, so C# reads these).
function map(kind, w, h) {
  const img = new ImageData(w, h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w; x++) {
    const u = x / (w - 1 || 1), v = y / (h - 1 || 1);
    let px, py, pz;
    if (kind === 'sphere') { const a = 2 * Math.PI * u, b = Math.PI * v; px = Math.sin(b) * Math.cos(a) * 0.5; py = Math.sin(b) * Math.sin(a) * 0.5; pz = Math.cos(b) * 0.5; }
    else if (kind === 'torus') { const a = 2 * Math.PI * u, b = 2 * Math.PI * v, R = 0.32, r = 0.16; px = (R + r * Math.cos(b)) * Math.cos(a); py = (R + r * Math.cos(b)) * Math.sin(a); pz = r * Math.sin(b); }
    else if (kind === 'plane') { px = u - 0.5; py = v - 0.5; pz = 0.15 * Math.sin(3 * Math.PI * u) * Math.cos(2 * Math.PI * v); }
    else if (kind === 'cylinder') { const a = 2 * Math.PI * u; const rr = 0.3 + 0.15 * Math.sin(Math.PI * v); px = rr * Math.cos(a); py = rr * Math.sin(a); pz = v - 0.5; }
    else { px = ((x * 37 + y * 11) % 97) / 97 - 0.5; py = ((x * 13 + y * 29) % 89) / 89 - 0.5; pz = ((x * 7 + y * 53) % 83) / 83 - 0.5; }
    const i = (y * w + x) * 4;
    img.data[i] = Math.round((px + 0.5) * 255); img.data[i + 1] = Math.round((py + 0.5) * 255); img.data[i + 2] = Math.round((pz + 0.5) * 255); img.data[i + 3] = 255;
  }
  return img;
}
const rgb = img => { const b = Buffer.alloc(img.width * img.height * 3); for (let p = 0; p < img.width * img.height; p++) { b[p*3]=img.data[p*4]; b[p*3+1]=img.data[p*4+1]; b[p*3+2]=img.data[p*4+2]; } return b.toString('base64'); };
const maps = {
  'sphere-64': map('sphere', 64, 64), 'torus-64': map('torus', 64, 64), 'plane-64': map('plane', 64, 64), 'cylinder-64': map('cylinder', 64, 64),
  'sphere-128x64': map('sphere', 128, 64), 'noise-32x16': map('noise', 32, 16), 'noise-7x5': map('noise', 7, 5), 'noise-2x2': map('noise', 2, 2), 'sphere-256': map('sphere', 256, 256),
};
const typeOf = { sphere: 1, torus: 2, plane: 3, cylinder: 4, noise: 0 };
const r6 = a => Array.from(a, v => Math.round(v * 1e6) / 1e6);
const cases = [];
for (const [name, img] of Object.entries(maps)) {
  const own = typeOf[name.split('-')[0]];
  const types = own ? [own] : [1, 2, 3, 4];
  for (const type of types) for (const lod of [6, 8, 16, 32, 64]) for (const [mirror, invert] of [[false, false], [true, false], [false, true], [true, true]]) {
    if ((name === 'sphere-256' || name === 'sphere-128x64' || lod === 64) && (mirror || invert)) continue;
    if (lod >= 32 && name.startsWith('noise') && type !== 1) continue;
    if (lod === 64 && name !== 'plane-64') continue;
    if (lod === 32 && (mirror || invert)) continue;
    const g = buildSculptGeometry(img, type, mirror, invert, lod);
    cases.push({ map: name, type, lod, mirror, invert, positions: r6(g.attributes.position.array), normals: r6(g.attributes.normal.array), uvs: r6(g.attributes.uv.array), indices: g.index });
  }
}
const out = { source: 'web-viewer client/src/mesh/sculpt.ts', maps: Object.fromEntries(Object.entries(maps).map(([k, v]) => [k, { width: v.width, height: v.height, rgb: rgb(v) }])), cases };
const gz = gzipSync(JSON.stringify(out), { level: 9 });
writeFileSync(process.argv[2], gz);
console.log('cases', cases.length, 'bytes', gz.length);
