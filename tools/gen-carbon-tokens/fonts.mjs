// Copies IBM Plex Sans / Mono (SIL OFL 1.1) into src/MySSH.UI.Carbon/Assets/Fonts.
// The npm packages only ship WOFF/WOFF2; WOFF 1.0 is just zlib-compressed sfnt tables, so it is
// unpacked back to a plain .ttf here (no font tooling needed). CJK Plex families are not bundled.
import { readFileSync, writeFileSync, mkdirSync, copyFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { inflateSync } from 'node:zlib';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const pkgDir = (p) => dirname(require.resolve(`${p}/package.json`));

const FONTS = [
  ['@ibm/plex-sans', 'IBMPlexSans-Regular'],
  ['@ibm/plex-sans', 'IBMPlexSans-Italic'],
  ['@ibm/plex-sans', 'IBMPlexSans-Medium'],
  ['@ibm/plex-sans', 'IBMPlexSans-SemiBold'],
  ['@ibm/plex-mono', 'IBMPlexMono-Regular'],
  ['@ibm/plex-mono', 'IBMPlexMono-Bold'],
];

export function woffToSfnt(woff) {
  if (woff.toString('latin1', 0, 4) !== 'wOFF') throw new Error('not a WOFF 1.0 file');
  const flavor = woff.readUInt32BE(4);
  const numTables = woff.readUInt16BE(12);
  const tables = [];
  for (let i = 0; i < numTables; i++) {
    const o = 44 + i * 20;
    const tag = woff.toString('latin1', o, o + 4);
    const offset = woff.readUInt32BE(o + 4);
    const compLength = woff.readUInt32BE(o + 8);
    const origLength = woff.readUInt32BE(o + 12);
    const checksum = woff.readUInt32BE(o + 16);
    const raw = woff.subarray(offset, offset + compLength);
    const data = compLength < origLength ? inflateSync(raw) : Buffer.from(raw);
    if (data.length !== origLength) throw new Error(`table ${tag}: bad length`);
    tables.push({ tag, checksum, data });
  }
  tables.sort((a, b) => (a.tag < b.tag ? -1 : a.tag > b.tag ? 1 : 0));
  let entrySelector = 0;
  while (1 << (entrySelector + 1) <= numTables) entrySelector++;
  const searchRange = (1 << entrySelector) * 16;
  const headerSize = 12 + numTables * 16;
  const pad4 = (n) => (n + 3) & ~3;
  const total = headerSize + tables.reduce((s, t) => s + pad4(t.data.length), 0);
  const out = Buffer.alloc(total);
  out.writeUInt32BE(flavor, 0);
  out.writeUInt16BE(numTables, 4);
  out.writeUInt16BE(searchRange, 6);
  out.writeUInt16BE(entrySelector, 8);
  out.writeUInt16BE(numTables * 16 - searchRange, 10);
  let offset = headerSize;
  tables.forEach((t, i) => {
    const r = 12 + i * 16;
    out.write(t.tag, r, 'latin1');
    out.writeUInt32BE(t.checksum, r + 4);
    out.writeUInt32BE(offset, r + 8);
    out.writeUInt32BE(t.data.length, r + 12);
    t.data.copy(out, offset);
    offset += pad4(t.data.length);
  });
  return out;
}

export function convertFonts(outDir) {
  mkdirSync(outDir, { recursive: true });
  for (const [pkg, name] of FONTS) {
    const woff = readFileSync(join(pkgDir(pkg), 'fonts', 'complete', 'woff', `${name}.woff`));
    writeFileSync(join(outDir, `${name}.ttf`), woffToSfnt(woff));
  }
  copyFileSync(join(pkgDir('@ibm/plex-sans'), 'LICENSE.txt'), join(outDir, 'OFL.txt'));
  console.log(`Fonts: ${FONTS.length} files -> ${outDir}`);
}
