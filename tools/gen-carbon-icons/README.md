# gen-carbon-icons

Converts a curated list of [`@carbon/icons`](https://github.com/carbon-design-system/carbon/tree/main/packages/icons)
(Apache-2.0) SVGs into Avalonia `StreamGeometry` resources. Outputs are committed:

- `src/MySSH.UI.Carbon/Icons/CarbonIcons.axaml` — keys `CarbonIcon.{kind}` (32px artwork), `CarbonIcon.{kind}.16` /
  `.20` (pixel-tuned artwork, when Carbon ships it) and `*.inner` (inner path of filled status icons).
- `src/MySSH.UI.Carbon/Icons/CarbonIconNames.g.cs` — `CarbonIconNames` constants used by `CarbonIcon.Kind`.
- `src/MySSH.UI.Carbon/Icons/LICENSE-carbon-icons.txt` — attribution + Apache-2.0 license text.

## Add an icon / regenerate

1. Add `'kind': 'carbon-file-name'` to `ICONS` in `gen.mjs` (file names are those under
   `node_modules/@carbon/icons/svg/32`, e.g. `chevron--down`). Only generic icons — no IBM product icons.
2. ```sh
   cd tools/gen-carbon-icons
   npm ci
   npm run gen
   ```
3. Check the Icons page of the gallery (`dotnet test tests/MySSH.UI.Carbon.Tests`, then
   `tests/MySSH.UI.Carbon.Tests/.work/screenshots/icons-light.png`).

The converter handles `<path>`, `<circle>`, `<rect>`, `<polygon>` and `translate` / `rotate` transforms, and emits
`F1` (non-zero) path data like the SVG default fill rule.
