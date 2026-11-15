// Generates the Carbon v11 design tokens for MySSH.UI.Carbon.
//
//   cd tools/gen-carbon-tokens && npm ci && npm run gen
//
// Outputs (committed, so the .NET build never needs node):
//   src/MySSH.UI.Carbon/Themes/Tokens.axaml      colors/brushes (theme dictionaries), spacing, sizes, type, motion
//   src/MySSH.UI.Carbon/Themes/Typography.axaml  TextBlock style classes (label-01, body-01, heading-03, ...)
//   src/MySSH.UI.Carbon/Assets/Fonts/*.ttf       IBM Plex Sans / Mono (converted from the npm WOFF files)
import * as themes from '@carbon/themes';
import * as type from '@carbon/type';
import * as layout from '@carbon/layout';
import * as motion from '@carbon/motion';
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { convertFonts } from './fonts.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const repo = join(here, '..', '..');
const outDir = join(repo, 'src', 'MySSH.UI.Carbon', 'Themes');
const require = createRequire(import.meta.url);
const ver = (p) => JSON.parse(readFileSync(require.resolve(`${p}/package.json`), 'utf8')).version;

const header = (what) => `<!--
  GENERATED FILE - DO NOT EDIT.
  ${what}
  Source: @carbon/themes ${ver('@carbon/themes')}, @carbon/type ${ver('@carbon/type')}, @carbon/layout ${ver('@carbon/layout')}, @carbon/motion ${ver('@carbon/motion')} (Apache-2.0).
  Regenerate: cd tools/gen-carbon-tokens && npm ci && npm run gen   (see tools/gen-carbon-tokens/README.md)
-->`;

// ---------------------------------------------------------------- colors
const pascal = (s) => s.charAt(0).toUpperCase() + s.slice(1);

function toAvaloniaColor(v) {
  if (typeof v !== 'string') return null;
  v = v.trim();
  let m = /^#([0-9a-f]{6})$/i.exec(v);
  if (m) return `#FF${m[1].toUpperCase()}`;
  m = /^#([0-9a-f]{3})$/i.exec(v);
  if (m) return `#FF${m[1].split('').map((c) => c + c).join('').toUpperCase()}`;
  m = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)$/i.exec(v);
  if (m) {
    const a = m[4] === undefined ? 1 : parseFloat(m[4]);
    const hex = (n) => Math.round(n).toString(16).padStart(2, '0').toUpperCase();
    return `#${hex(a * 255)}${hex(+m[1])}${hex(+m[2])}${hex(+m[3])}`;
  }
  return null;
}

// Variants: Avalonia Light = Carbon White, Dark = Carbon Gray 100. G10 / G90 are extra variants
// (CarbonThemeVariants.G10 inherits Light, CarbonThemeVariants.G90 inherits Dark).
const variants = [
  { key: 'Light', theme: themes.white, comp: 'whiteTheme', fallback: [] },
  { key: 'Dark', theme: themes.g100, comp: 'g100', fallback: ['g90'] },
  { key: '{x:Static carbon:CarbonThemeVariants.G10}', theme: themes.g10, comp: 'g10', fallback: ['whiteTheme'] },
  { key: '{x:Static carbon:CarbonThemeVariants.G90}', theme: themes.g90, comp: 'g90', fallback: ['g100'] },
];

const skip = /^(syntax|ai|chat)/;
const semanticNames = Object.keys(themes.white).filter(
  (k) => !skip.test(k) && toAvaloniaColor(themes.white[k]) !== null,
);

const componentGroups = ['buttonTokens', 'tagTokens', 'notificationTokens', 'statusTokens', 'contentSwitcherTokens'];
const componentNames = [];
for (const g of componentGroups) for (const k of Object.keys(themes[g])) componentNames.push([g, k]);

function componentValue(g, k, v) {
  const entry = themes[g][k];
  for (const c of [v.comp, ...v.fallback]) if (entry[c] !== undefined) return toAvaloniaColor(entry[c]);
  return null;
}

// Simple-theme compatibility keys, so controls without a Carbon template still look Carbon-ish.
const withAlpha = (c, a) => `#${a}${c.slice(3)}`;
function simpleCompat(t) {
  const c = (k) => toAvaloniaColor(t[k]);
  const map = {
    ThemeBackground: c('background'),
    ThemeForeground: c('textPrimary'),
    ThemeForegroundLow: c('textSecondary'),
    ThemeBorderLow: c('borderSubtle01'),
    ThemeBorderMid: c('borderStrong01'),
    ThemeBorderHigh: c('borderInverse'),
    ThemeControlLow: c('borderStrong01'),
    ThemeControlMid: c('layer01'),
    ThemeControlMidHigh: c('layerAccent01'),
    ThemeControlHigh: c('layerActive01'),
    ThemeControlVeryHigh: c('borderInverse'),
    ThemeControlHighlightLow: c('layerHover01'),
    ThemeControlHighlightMid: c('layerSelected01'),
    ThemeControlHighlightHigh: c('layerActive01'),
    ThemeAccent: c('interactive'),
    ThemeAccent2: withAlpha(c('interactive'), '99'),
    ThemeAccent3: withAlpha(c('interactive'), '66'),
    ThemeAccent4: withAlpha(c('interactive'), '33'),
    Highlight: c('interactive'),
    Highlight2: c('interactive'),
    HighlightForeground: c('textOnColor'),
    Error: c('supportError'),
    ErrorLow: withAlpha(c('supportError'), '10'),
  };
  return map;
}

function colorBlock(v) {
  const lines = [];
  const add = (name, color, comment) => {
    if (!color) return;
    if (comment) lines.push(`      <!-- ${comment} -->`);
    lines.push(`      <Color x:Key="Carbon${name}Color">${color}</Color>`);
    lines.push(`      <SolidColorBrush x:Key="Carbon${name}" Color="${color}" />`);
  };
  for (const k of semanticNames) add(pascal(k), toAvaloniaColor(v.theme[k]));
  for (const [g, k] of componentNames) add(pascal(k), componentValue(g, k, v));
  lines.push('      <!-- Avalonia.Themes.Simple compatibility (controls without a Carbon template) -->');
  for (const [k, color] of Object.entries(simpleCompat(v.theme))) {
    // Simple uses "HighlightColor"/"HighlightBrush", "ErrorColor"/"ErrorBrush" and Theme*Color/Theme*Brush.
    lines.push(`      <Color x:Key="${k}Color">${color}</Color>`);
    lines.push(`      <SolidColorBrush x:Key="${k}Brush" Color="${color}" />`);
  }
  return lines.join('\n');
}

// ---------------------------------------------------------------- spacing / sizes
const rem = (s) => (typeof s === 'number' ? s : parseFloat(s) * (s.endsWith('rem') ? 16 : 1));
const fmt = (n) => +n.toFixed(4) + '';

const spacingLines = [];
layout.spacing.forEach((s, i) => {
  const n = String(i + 1).padStart(2, '0');
  spacingLines.push(`  <x:Double x:Key="CarbonSpacing${n}">${fmt(rem(s))}</x:Double>`);
  spacingLines.push(`  <Thickness x:Key="CarbonSpacing${n}Thickness">${fmt(rem(s))}</Thickness>`);
});
const sizeLines = [
  ['XSmall', layout.sizeXSmall], ['Small', layout.sizeSmall], ['Medium', layout.sizeMedium],
  ['Large', layout.sizeLarge], ['XLarge', layout.sizeXLarge], ['2XLarge', layout.size2XLarge],
].map(([n, v]) => `  <x:Double x:Key="CarbonSize${n}">${fmt(rem(v))}</x:Double>`);
sizeLines.push(`  <x:Double x:Key="CarbonIconSize01">${fmt(rem(layout.iconSize01))}</x:Double>`);
sizeLines.push(`  <x:Double x:Key="CarbonIconSize02">${fmt(rem(layout.iconSize02))}</x:Double>`);

// ---------------------------------------------------------------- type
const typeStyles = [
  'label01', 'label02', 'helperText01', 'helperText02', 'legal01', 'legal02',
  'bodyCompact01', 'bodyCompact02', 'body01', 'body02', 'code01', 'code02',
  'headingCompact01', 'headingCompact02', 'heading01', 'heading02', 'heading03', 'heading04', 'heading05',
];
const kebab = (s) => s.replace(/([a-z])([A-Z])/g, '$1-$2').replace(/([a-zA-Z])(\d)/g, '$1-$2').toLowerCase();
const weightName = (w) => ({ 300: 'Light', 400: 'Normal', 500: 'Medium', 600: 'SemiBold', 700: 'Bold' })[w ?? 400];

const typeLines = [];
const typographyStyles = [];
for (const k of typeStyles) {
  const s = type.styles[k] ?? type[k];
  const size = rem(s.fontSize);
  const lh = size * s.lineHeight;
  const ls = typeof s.letterSpacing === 'string' ? parseFloat(s.letterSpacing) : (s.letterSpacing ?? 0);
  const P = pascal(k);
  typeLines.push(`  <x:Double x:Key="Carbon${P}FontSize">${fmt(size)}</x:Double>`);
  typeLines.push(`  <x:Double x:Key="Carbon${P}LineHeight">${fmt(Math.round(lh * 100) / 100)}</x:Double>`);
  typeLines.push(`  <FontWeight x:Key="Carbon${P}FontWeight">${weightName(s.fontWeight)}</FontWeight>`);
  typeLines.push(`  <x:Double x:Key="Carbon${P}LetterSpacing">${fmt(ls)}</x:Double>`);
  const mono = k.startsWith('code');
  typographyStyles.push(`  <Style Selector=":is(TextBlock).${kebab(k)}">
${mono ? '    <Setter Property="FontFamily" Value="{DynamicResource CarbonFontMono}" />\n' : ''}    <Setter Property="FontSize" Value="${fmt(size)}" />
    <Setter Property="LineHeight" Value="${fmt(Math.round(lh * 100) / 100)}" />
    <Setter Property="FontWeight" Value="${weightName(s.fontWeight)}" />
    <Setter Property="LetterSpacing" Value="${fmt(ls)}" />
  </Style>`);
}

// ---------------------------------------------------------------- motion
const ms = (s) => parseFloat(s);
const durations = [
  ['Fast01', motion.fast01], ['Fast02', motion.fast02], ['Moderate01', motion.moderate01],
  ['Moderate02', motion.moderate02], ['Slow01', motion.slow01], ['Slow02', motion.slow02],
];
const motionLines = durations.map(
  ([n, v]) => `  <x:TimeSpan x:Key="CarbonDuration${n}">0:0:${(ms(v) / 1000).toFixed(3)}</x:TimeSpan>`,
);
for (const [group, values] of Object.entries(motion.easings)) {
  for (const [mode, css] of Object.entries(values)) {
    const [x1, y1, x2, y2] = /cubic-bezier\(([^)]+)\)/.exec(css)[1].split(',').map((x) => +x.trim());
    motionLines.push(
      `  <SplineEasing x:Key="CarbonEasing${pascal(group)}${pascal(mode)}" X1="${x1}" Y1="${y1}" X2="${x2}" Y2="${y2}" />`,
    );
  }
}

// ---------------------------------------------------------------- write
const tokens = `${header('Carbon Design System v11 tokens for Avalonia: Light = White, Dark = Gray 100, plus G10 / G90 variants.')}
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:carbon="using:MySSH.UI.Carbon">
  <!-- Color tokens: Carbon{Token} is a SolidColorBrush, Carbon{Token}Color the Color (e.g. CarbonLayer01, CarbonTextPrimaryColor). -->
  <ResourceDictionary.ThemeDictionaries>
${variants
  .map((v) => `    <ResourceDictionary x:Key="${v.key}">\n${colorBlock(v)}\n    </ResourceDictionary>`)
  .join('\n')}
  </ResourceDictionary.ThemeDictionaries>

  <!-- Spacing scale (spacing-01..13, px) -->
${spacingLines.join('\n')}

  <!-- Component sizes (xs 24, sm 32, md 40, lg 48, xl 64, 2xl 80) and icon sizes -->
${sizeLines.join('\n')}

  <!-- Type tokens -->
${typeLines.join('\n')}

  <!-- Motion: durations and productive / expressive easings -->
${motionLines.join('\n')}
</ResourceDictionary>
`;

const typography = `${header('Carbon productive type styles as TextBlock classes, e.g. <TextBlock Classes="heading-03" />.')}
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
${typographyStyles.join('\n')}
</Styles>
`;

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, 'Tokens.axaml'), tokens);
writeFileSync(join(outDir, 'Typography.axaml'), typography);
console.log(`Tokens.axaml: ${semanticNames.length} semantic + ${componentNames.length} component color tokens x ${variants.length} variants`);
console.log(`Typography.axaml: ${typeStyles.length} type styles`);

convertFonts(join(repo, 'src', 'MySSH.UI.Carbon', 'Assets', 'Fonts'));
