# gen-app-icon

Generates every MySSH app-icon asset from one geometry definition: the `>_` prompt mark (Carbon Blue 60
`#0f62fe`) on a Carbon Gray 100 `#161616` tile. Outputs are committed; regenerate after changing the geometry
or colors in `gen.swift`. Requires macOS (CoreGraphics, `iconutil`).

```sh
swift tools/gen-app-icon/gen.swift   # from the repository root
```

| Output | Use |
|---|---|
| `assets/icons/symbol.svg` | Bare mark on a 32×32 grid, transparent |
| `assets/icons/app-icon.svg` | Full-bleed rounded tile (source for Windows / Linux / window icon) |
| `assets/icons/app-icon-macos.svg` | macOS tile on the Apple icon grid (824/1024, transparent margin) |
| `assets/icons/icon.png` | 1024 full-bleed PNG (Linux packages) |
| `assets/icons/icon.ico` | 16–256 full-bleed, PNG entries (`ApplicationIcon` of MySSH.Desktop) |
| `assets/icons/icon.icns` | 16–1024 macOS tile (app bundle, P5 packaging) |
| `assets/logo.png` | 512 macOS tile (README) |
| `src/MySSH.Desktop/Assets/icon.png` | 256 full-bleed window icon (Windows / Linux) |
| `src/MySSH.Desktop/Assets/icon-macos.png` | 512 macOS tile, Dock icon when running unbundled |

Sizes ≤ 32 px use a slightly larger mark so the chevron arms stay about 1.6 px wide.
The concept came from a Codex design pass (concept 1 of 3, "Forward").
