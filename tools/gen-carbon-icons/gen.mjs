// Converts a curated list of @carbon/icons SVGs into Avalonia StreamGeometry resources.
//
//   cd tools/gen-carbon-icons && npm ci && npm run gen
//
// Output (committed): src/MySSH.UI.Carbon/Icons/CarbonIcons.axaml + Icons/CarbonIconNames.g.cs
// Resource keys: CarbonIcon.{kind}            32px artwork (viewBox 0 0 32 32), always present
//                CarbonIcon.{kind}.16 / .20   pixel-tuned artwork when Carbon ships it
//                CarbonIcon.{kind}.inner      "inner path" of filled status icons (drawn in a contrast color)
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const require = createRequire(import.meta.url);
const iconsDir = dirname(require.resolve('@carbon/icons/package.json'));
const version = JSON.parse(readFileSync(join(iconsDir, 'package.json'), 'utf8')).version;

// kind (used by CarbonIcon.Kind) -> @carbon/icons file name
const ICONS = {
  'add': 'add',
  'close': 'close',
  'search': 'search',
  'settings': 'settings',
  'terminal': 'terminal',
  'folder': 'folder',
  'folder-open': 'folder--open',
  'document': 'document',
  'chevron-down': 'chevron--down',
  'chevron-up': 'chevron--up',
  'chevron-left': 'chevron--left',
  'chevron-right': 'chevron--right',
  'caret-down': 'caret--down',
  'caret-up': 'caret--up',
  'overflow-menu-vertical': 'overflow-menu--vertical',
  'overflow-menu-horizontal': 'overflow-menu--horizontal',
  'edit': 'edit',
  'trash-can': 'trash-can',
  'copy': 'copy',
  'password': 'password',
  'user': 'user',
  'server': 'bare-metal-server',
  'virtual-machine': 'virtual-machine',
  'database': 'data--base',
  'server-proxy': 'server--proxy',
  'connect': 'connect',
  'plug': 'plug',
  'network': 'network--3',
  'view': 'view',
  'view-off': 'view--off',
  'warning': 'warning',
  'warning-filled': 'warning--filled',
  'warning-alt-filled': 'warning--alt--filled',
  'error': 'error',
  'error-filled': 'error--filled',
  'misuse': 'misuse',
  'checkmark': 'checkmark',
  'checkmark-filled': 'checkmark--filled',
  'checkmark-outline': 'checkmark--outline',
  'information': 'information',
  'information-filled': 'information--filled',
  'help': 'help',
  'upload': 'upload',
  'download': 'download',
  'renew': 'renew',
  'launch': 'launch',
  'menu': 'menu',
  'chat': 'chat',
  'machine-learning': 'machine-learning',
  'magic-wand': 'magic-wand',
  'locked': 'locked',
  'unlocked': 'unlocked',
  'filter': 'filter',
  'sort-ascending': 'sort--ascending',
  'sort-descending': 'sort--descending',
  'arrows-vertical': 'arrows--vertical',
  'arrow-up': 'arrow--up',
  'arrow-down': 'arrow--down',
  'arrow-left': 'arrow--left',
  'arrow-right': 'arrow--right',
  'home': 'home',
  'subtract': 'subtract',
  'logout': 'logout',
  'time': 'time',
  'cloud': 'cloud',
  'pin': 'pin',
  'star': 'star',
  'light': 'light',
  'asleep': 'asleep',
  'translate': 'translate',
  'notification': 'notification',
  'switcher': 'switcher',
  'save': 'save',
  'play': 'play',
  'stop': 'stop',
  'list': 'list',
  'data-table': 'data-table',
  'tag': 'tag',
  'text-font': 'text--font',
  'color-palette': 'color-palette',
  'application': 'application',
};

// ------------------------------------------------------------------ SVG -> path data
// Tokenises SVG path data properly (arc flags may be packed like "011") and re-emits it with spaces,
// so Avalonia's PathMarkupParser gets unambiguous input.
function normalizePath(d) {
  const out = [];
  let i = 0;
  let cmd = null;
  let argIndex = 0;
  const num = /^[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?/;
  while (i < d.length) {
    const c = d[i];
    if (/[\s,]/.test(c)) { i++; continue; }
    if (/[MmLlHhVvCcSsQqTtAaZz]/.test(c)) { cmd = c; argIndex = 0; out.push(c); i++; continue; }
    if (cmd && /[Aa]/.test(cmd) && (argIndex % 7 === 3 || argIndex % 7 === 4)) {
      out.push(d[i]); i++; argIndex++; continue; // single-char flag
    }
    const m = num.exec(d.slice(i));
    if (!m) throw new Error(`bad path data near "${d.slice(i, i + 20)}"`);
    out.push(String(+m[0]));
    i += m[0].length;
    argIndex++;
  }
  return out.join(' ');
}

// Applies an SVG transform (translate / rotate only - all Carbon uses) by converting the path to
// absolute commands and mapping every point. Arcs keep their radii and gain the rotation angle.
function parseTransform(t) {
  let m = /^translate\(\s*([-\d.]+)(?:[\s,]+([-\d.]+))?\s*\)$/.exec(t);
  if (m) { const tx = +m[1], ty = +(m[2] ?? 0); return { angle: 0, map: (x, y) => [x + tx, y + ty] }; }
  m = /^rotate\(\s*([-\d.]+)(?:[\s,]+([-\d.]+)[\s,]+([-\d.]+))?\s*\)$/.exec(t);
  if (m) {
    const a = (+m[1] * Math.PI) / 180, cx = +(m[2] ?? 0), cy = +(m[3] ?? 0);
    const cos = Math.cos(a), sin = Math.sin(a);
    return { angle: +m[1], map: (x, y) => [cx + (x - cx) * cos - (y - cy) * sin, cy + (x - cx) * sin + (y - cy) * cos] };
  }
  throw new Error(`unsupported transform ${t}`);
}

function transformPath(normalized, t) {
  const { angle, map } = parseTransform(t);
  const tok = normalized.split(' ');
  const out = [];
  let i = 0, cmd = '', x = 0, y = 0, sx = 0, sy = 0;
  const r = (n) => String(+n.toFixed(4));
  const pt = (px, py) => { const [a, b] = map(px, py); return `${r(a)} ${r(b)}`; };
  const next = () => +tok[i++];
  const isCmd = (s) => /^[A-Za-z]$/.test(s);
  while (i < tok.length) {
    if (isCmd(tok[i])) cmd = tok[i++];
    const rel = cmd === cmd.toLowerCase();
    const C = cmd.toUpperCase();
    const ax = (v) => (rel ? x + v : v), ay = (v) => (rel ? y + v : v);
    switch (C) {
      case 'Z': out.push('Z'); x = sx; y = sy; break;
      case 'M': { const nx = ax(next()), ny = ay(next()); out.push('M', pt(nx, ny)); x = sx = nx; y = sy = ny; cmd = rel ? 'l' : 'L'; break; }
      case 'L': { const nx = ax(next()), ny = ay(next()); out.push('L', pt(nx, ny)); x = nx; y = ny; break; }
      case 'H': { const nx = ax(next()); out.push('L', pt(nx, y)); x = nx; break; }
      case 'V': { const ny = rel ? y + next() : next(); out.push('L', pt(x, ny)); y = ny; break; }
      case 'C': { const a = [ax(next()), ay(next()), ax(next()), ay(next()), ax(next()), ay(next())];
        out.push('C', pt(a[0], a[1]), pt(a[2], a[3]), pt(a[4], a[5])); x = a[4]; y = a[5]; break; }
      case 'S': case 'Q': { const a = [ax(next()), ay(next()), ax(next()), ay(next())];
        out.push(C, pt(a[0], a[1]), pt(a[2], a[3])); x = a[2]; y = a[3]; break; }
      case 'T': { const nx = ax(next()), ny = ay(next()); out.push('T', pt(nx, ny)); x = nx; y = ny; break; }
      case 'A': { const rx = next(), ry = next(), rot = next(), large = tok[i++], sweep = tok[i++];
        const nx = ax(next()), ny = ay(next());
        out.push('A', `${r(rx)} ${r(ry)} ${r(rot + angle)} ${large} ${sweep}`, pt(nx, ny)); x = nx; y = ny; break; }
      default: throw new Error(`bad command ${cmd}`);
    }
    if (C === 'Z' && i < tok.length && !isCmd(tok[i])) throw new Error('numbers after Z');
  }
  return out.join(' ');
}

const attr = (el, name) => {
  const m = new RegExp(`\\s${name}="([^"]*)"`).exec(el);
  return m ? m[1] : null;
};

function elementToPath(el) {
  const tag = /^<(\w+)/.exec(el)[1];
  const p = shapeToPath(el, tag);
  const t = attr(el, 'transform');
  return t ? transformPath(p, t) : p;
}

function shapeToPath(el, tag) {
  switch (tag) {
    case 'path':
      return normalizePath(attr(el, 'd'));
    case 'circle': {
      const cx = +attr(el, 'cx'), cy = +attr(el, 'cy'), r = +attr(el, 'r');
      return `M ${cx - r} ${cy} A ${r} ${r} 0 1 0 ${cx + r} ${cy} A ${r} ${r} 0 1 0 ${cx - r} ${cy} Z`;
    }
    case 'rect': {
      const x = +(attr(el, 'x') ?? 0), y = +(attr(el, 'y') ?? 0), w = +attr(el, 'width'), h = +attr(el, 'height');
      return `M ${x} ${y} H ${x + w} V ${y + h} H ${x} Z`;
    }
    case 'polygon':
      return `M ${normalizePath('L' + attr(el, 'points')).slice(2)} Z`;
    default:
      throw new Error(`unsupported element <${tag}>`);
  }
}

function convert(file) {
  const svg = readFileSync(file, 'utf8');
  const viewBox = attr(svg, 'viewBox');
  const elements = svg.match(/<(path|circle|rect|polygon)\b[^>]*>/g) ?? [];
  const main = [], inner = [];
  for (const el of elements) {
    const isInner = attr(el, 'data-icon-path') === 'inner-path';
    if (!isInner && attr(el, 'fill') === 'none') continue;
    (isInner ? inner : main).push(elementToPath(el));
  }
  // F1 = NonZero, the SVG default fill rule.
  return { viewBox, main: 'F1 ' + main.join(' '), inner: inner.length ? 'F1 ' + inner.join(' ') : null };
}

// ------------------------------------------------------------------ emit
const lines = [];
const kinds = [];
for (const [kind, name] of Object.entries(ICONS)) {
  const f32 = join(iconsDir, 'svg', '32', `${name}.svg`);
  if (!existsSync(f32)) throw new Error(`missing icon ${name}`);
  const g = convert(f32);
  if (g.viewBox !== '0 0 32 32') throw new Error(`${name}: unexpected viewBox ${g.viewBox}`);
  lines.push(`  <!-- ${kind} -->`);
  lines.push(`  <StreamGeometry x:Key="CarbonIcon.${kind}">${g.main}</StreamGeometry>`);
  if (g.inner) lines.push(`  <StreamGeometry x:Key="CarbonIcon.${kind}.inner">${g.inner}</StreamGeometry>`);
  const sizes = [32];
  for (const s of [16, 20]) {
    const f = join(iconsDir, 'svg', String(s), `${name}.svg`);
    if (!existsSync(f)) continue;
    const gs = convert(f);
    if (gs.viewBox !== `0 0 ${s} ${s}`) throw new Error(`${name}/${s}: unexpected viewBox ${gs.viewBox}`);
    lines.push(`  <StreamGeometry x:Key="CarbonIcon.${kind}.${s}">${gs.main}</StreamGeometry>`);
    if (gs.inner) lines.push(`  <StreamGeometry x:Key="CarbonIcon.${kind}.${s}.inner">${gs.inner}</StreamGeometry>`);
    sizes.push(s);
  }
  kinds.push([kind, sizes]);
}

const axaml = `<!--
  GENERATED FILE - DO NOT EDIT.
  Carbon icons as StreamGeometry resources. Source: @carbon/icons ${version} (Apache-2.0, see Icons/LICENSE-carbon-icons.txt).
  Regenerate: cd tools/gen-carbon-icons && npm ci && npm run gen   (edit the ICONS list in gen.mjs)
  Keys: CarbonIcon.{kind} (32px artwork), CarbonIcon.{kind}.16 / .20 (pixel-tuned, when available), *.inner (inner path of filled icons).
-->
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
${lines.join('\n')}
</ResourceDictionary>
`;

const cs = `// <auto-generated>
// Generated by tools/gen-carbon-icons from @carbon/icons ${version}. Do not edit.
// </auto-generated>
namespace MySSH.UI.Carbon;

/// <summary>Names accepted by <see cref="CarbonIcon.Kind"/>.</summary>
public static class CarbonIconNames
{
${kinds.map(([k]) => `    public const string ${k.split('-').map((p) => p[0].toUpperCase() + p.slice(1)).join('')} = "${k}";`).join('\n')}

    /// <summary>All icon kinds, in catalogue order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
${kinds.map(([k]) => `        "${k}",`).join('\n')}
    ];

    /// <summary>Kinds that ship pixel-tuned 16px artwork.</summary>
    internal static IReadOnlySet<string> Has16 { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
${kinds.filter(([, s]) => s.includes(16)).map(([k]) => `        "${k}",`).join('\n')}
    };

    /// <summary>Kinds that ship pixel-tuned 20px artwork.</summary>
    internal static IReadOnlySet<string> Has20 { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
${kinds.filter(([, s]) => s.includes(20)).map(([k]) => `        "${k}",`).join('\n')}
    };
}
`;

const outDir = join(repo, 'src', 'MySSH.UI.Carbon', 'Icons');
mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, 'CarbonIcons.axaml'), axaml);
writeFileSync(join(outDir, 'CarbonIconNames.g.cs'), cs);
writeFileSync(join(outDir, 'LICENSE-carbon-icons.txt'),
  `The icon artwork in CarbonIcons.axaml is derived from @carbon/icons ${version}\n` +
  `(https://github.com/carbon-design-system/carbon/tree/main/packages/icons),\n` +
  `Copyright IBM Corp., licensed under the Apache License, Version 2.0.\n` +
  `The SVG paths were converted to Avalonia StreamGeometry syntax; no other changes were made.\n\n` +
  readFileSync(join(iconsDir, 'LICENSE'), 'utf8'));
console.log(`CarbonIcons.axaml: ${kinds.length} icons`);
