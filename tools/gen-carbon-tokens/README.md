# gen-carbon-tokens

Generates the Carbon Design System v11 resources for `src/MySSH.UI.Carbon` and copies the IBM Plex fonts.
The outputs are committed, so building the .NET solution never needs Node.

| Output | Content |
|---|---|
| `src/MySSH.UI.Carbon/Themes/Tokens.axaml` | Color tokens as `Carbon{Token}` (SolidColorBrush) + `Carbon{Token}Color` inside `ThemeDictionaries` (Light = White, Dark = Gray 100, plus `CarbonThemeVariants.G10` / `G90`); component tokens (button, tag, notification, status, content switcher); `Theme*` keys that re-color Avalonia.Themes.Simple; spacing (`CarbonSpacing01..13`, `...Thickness`), sizes, type tokens (`Carbon{Style}FontSize/LineHeight/FontWeight/LetterSpacing`) and motion (`CarbonDuration*`, `CarbonEasing*`). |
| `src/MySSH.UI.Carbon/Themes/Typography.axaml` | TextBlock classes `label-01`, `helper-text-01`, `body-compact-01`, `body-01`, `heading-compact-01`, `heading-01..05`, `code-01/02`, ... |
| `src/MySSH.UI.Carbon/Assets/Fonts/*.ttf`, `OFL.txt` | IBM Plex Sans (Regular, Italic, Medium, SemiBold) and IBM Plex Mono (Regular, Bold), SIL OFL 1.1. The npm packages only ship WOFF/WOFF2; `fonts.mjs` unpacks WOFF 1.0 back to TTF (zlib only). |

## Regenerate

```sh
cd tools/gen-carbon-tokens
npm ci          # versions are pinned in package.json / package-lock.json
npm run gen
```

Then rebuild and run `dotnet test tests/MySSH.UI.Carbon.Tests` (token tests + gallery screenshots).

To bump Carbon, change the pinned versions in `package.json`, run `npm install`, regenerate and review the
gallery screenshots. To add a token family, edit `skip` / `componentGroups` in `gen.mjs`.

CJK Plex families (IBM Plex Sans SC / JP) are intentionally not bundled (size); CJK text falls back to the
platform font.
