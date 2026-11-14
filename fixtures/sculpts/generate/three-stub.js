// Copyright (c) 2026 Legion Builds
// SPDX-License-Identifier: LGPL-2.1-or-later

// Minimal stand-in for the two three.js classes sculpt.ts uses (scratch oracle only).
export class BufferAttribute { constructor(array, itemSize) { this.array = array; this.itemSize = itemSize; } }
export class BufferGeometry {
  constructor() { this.attributes = {}; this.index = null; this.groups = []; }
  setAttribute(n, a) { this.attributes[n] = a; }
  setIndex(i) { this.index = i; }
  addGroup(s, c, m) { this.groups.push({ s, c, m }); }
  computeBoundingSphere() {}
}
