// Generates every MySSH app-icon asset from one geometry definition (the ">_" prompt mark).
// macOS only (CoreGraphics + iconutil). Outputs are committed; rerun after changing the geometry:
//
//   swift tools/gen-app-icon/gen.swift            (from the repository root)
//
// Outputs:
//   assets/icons/symbol.svg            the bare mark on a 32x32 grid (Blue 60, transparent)
//   assets/icons/app-icon.svg          full-bleed tile (window icon, Windows, Linux)
//   assets/icons/app-icon-macos.svg    macOS tile: 824px squircle on a 1024 canvas (Apple icon grid)
//   assets/icons/icon.png              1024 full-bleed PNG (Linux packages)
//   assets/icons/icon.ico              16-256 full-bleed (Windows ApplicationIcon)
//   assets/icons/icon.icns             16-1024 macOS tile (app bundle)
//   assets/logo.png                    512 macOS tile (README)
//   src/MySSH.Desktop/Assets/icon.png        256 full-bleed (window icon on Windows / Linux)
//   src/MySSH.Desktop/Assets/icon-macos.png  512 macOS tile (Dock icon when running unbundled)

import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// ---------------------------------------------------------------- geometry (32x32 grid)

/// Prompt chevron ">" and cursor "_". Both have a 4-unit stroke weight (the chevron arms are 3*sqrt(2) ~ 4.24).
let chevron: [CGPoint] = [(5, 6), (15, 16), (5, 26), (2, 23), (9, 16), (2, 9)].map { CGPoint(x: $0.0, y: $0.1) }
let cursor = CGRect(x: 17, y: 22, width: 11, height: 4)
/// Visual bounds of the mark: x 2...28, y 6...26.
let markBounds = CGRect(x: 2, y: 6, width: 26, height: 20)

let blue60 = "#0f62fe"   // Carbon Blue 60 (interactive)
let gray100 = "#161616"  // Carbon Gray 100 (UI shell)

enum Tile {
    /// Rounded square filling the canvas.
    case fullBleed
    /// Apple macOS icon grid: 824/1024 tile, ~22.5% corner radius, transparent margin for the shadow area.
    case macOS
}

struct Layout {
    let tile: CGRect
    let radius: CGFloat
    let scale: CGFloat
    let origin: CGPoint
}

func layout(_ kind: Tile, size: CGFloat) -> Layout {
    let tile: CGRect
    let radius: CGFloat
    let markWidth: CGFloat
    switch kind {
    case .fullBleed:
        tile = CGRect(x: 0, y: 0, width: size, height: size)
        radius = size * 0.2
        // Small sizes get a larger mark so the chevron arms stay >= ~1.6px.
        markWidth = size * (size <= 32 ? 0.68 : 0.6)
    case .macOS:
        let inset = size * 100 / 1024
        tile = CGRect(x: inset, y: inset, width: size - 2 * inset, height: size - 2 * inset)
        radius = tile.width * 0.225
        markWidth = tile.width * 0.58
    }
    let scale = markWidth / markBounds.width
    let origin = CGPoint(x: tile.midX - markBounds.midX * scale, y: tile.midY - markBounds.midY * scale)
    return Layout(tile: tile, radius: radius, scale: scale, origin: origin)
}

// ---------------------------------------------------------------- SVG

func fmt(_ v: CGFloat) -> String {
    let r = (v * 100).rounded() / 100
    return r == r.rounded() ? String(Int(r)) : String(format: "%.2f", Double(r)).replacingOccurrences(of: #"0+$"#, with: "", options: .regularExpression)
}

func markPaths(_ l: Layout?) -> String {
    let s = l?.scale ?? 1, o = l?.origin ?? .zero
    func p(_ pt: CGPoint) -> String { "\(fmt(o.x + pt.x * s)) \(fmt(o.y + pt.y * s))" }
    let c = cursor
    let chev = "M" + chevron.map(p).joined(separator: " ") + "Z"
    let cur = "M\(p(c.origin))H\(fmt(o.x + c.maxX * s))V\(fmt(o.y + c.maxY * s))H\(fmt(o.x + c.minX * s))Z"
    return "  <path d=\"\(chev)\"/>\n  <path d=\"\(cur)\"/>\n"
}

func symbolSVG() -> String {
    "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 32 32\" fill=\"\(blue60)\">\n" + markPaths(nil) + "</svg>\n"
}

func tileSVG(_ kind: Tile) -> String {
    let l = layout(kind, size: 1024)
    let t = l.tile
    return """
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024">
      <rect x="\(fmt(t.minX))" y="\(fmt(t.minY))" width="\(fmt(t.width))" height="\(fmt(t.height))" rx="\(fmt(l.radius))" fill="\(gray100)"/>
      <g fill="\(blue60)">
    \(markPaths(l).split(separator: "\n").map { "  " + $0 }.joined(separator: "\n"))
      </g>
    </svg>

    """
}

// ---------------------------------------------------------------- raster

func color(_ hex: String) -> CGColor {
    let v = Int(hex.dropFirst(), radix: 16)!
    return CGColor(srgbRed: CGFloat((v >> 16) & 0xff) / 255, green: CGFloat((v >> 8) & 0xff) / 255,
                   blue: CGFloat(v & 0xff) / 255, alpha: 1)
}

func render(_ kind: Tile, size px: Int) -> CGImage {
    let size = CGFloat(px)
    let ctx = CGContext(data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.translateBy(x: 0, y: size) // SVG coordinates: y down
    ctx.scaleBy(x: 1, y: -1)
    ctx.setShouldAntialias(true)
    let l = layout(kind, size: size)

    ctx.addPath(CGPath(roundedRect: l.tile, cornerWidth: l.radius, cornerHeight: l.radius, transform: nil))
    ctx.setFillColor(color(gray100))
    ctx.fillPath()

    func t(_ p: CGPoint) -> CGPoint { CGPoint(x: l.origin.x + p.x * l.scale, y: l.origin.y + p.y * l.scale) }
    ctx.setFillColor(color(blue60))
    ctx.addLines(between: chevron.map(t))
    ctx.closePath()
    ctx.fillPath()
    let c0 = t(cursor.origin), c1 = t(CGPoint(x: cursor.maxX, y: cursor.maxY))
    ctx.fill(CGRect(x: c0.x, y: c0.y, width: c1.x - c0.x, height: c1.y - c0.y))
    return ctx.makeImage()!
}

func pngData(_ image: CGImage) -> Data {
    let data = NSMutableData()
    let dest = CGImageDestinationCreateWithData(data, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    precondition(CGImageDestinationFinalize(dest))
    return data as Data
}

/// ICO with PNG-compressed entries (supported since Windows Vista).
func icoData(_ sizes: [Int]) -> Data {
    let images = sizes.map { pngData(render(.fullBleed, size: $0)) }
    var out = Data()
    func u16(_ v: Int) { out.append(contentsOf: [UInt8(v & 0xff), UInt8(v >> 8 & 0xff)]) }
    func u32(_ v: Int) { u16(v & 0xffff); u16(v >> 16 & 0xffff) }
    u16(0); u16(1); u16(sizes.count)
    var offset = 6 + 16 * sizes.count
    for (size, png) in zip(sizes, images) {
        out.append(UInt8(size >= 256 ? 0 : size)); out.append(UInt8(size >= 256 ? 0 : size))
        out.append(0); out.append(0) // palette, reserved
        u16(1); u16(32)              // planes, bpp
        u32(png.count); u32(offset)
        offset += png.count
    }
    images.forEach { out.append($0) }
    return out
}

// ---------------------------------------------------------------- outputs

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
guard FileManager.default.fileExists(atPath: root.appendingPathComponent("MySSH.slnx").path) else {
    FileHandle.standardError.write("Run from the repository root.\n".data(using: .utf8)!)
    exit(1)
}
func write(_ rel: String, _ data: Data) throws {
    let url = root.appendingPathComponent(rel)
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
    try data.write(to: url)
    print("wrote \(rel) (\(data.count) bytes)")
}

try write("assets/icons/symbol.svg", Data(symbolSVG().utf8))
try write("assets/icons/app-icon.svg", Data(tileSVG(.fullBleed).utf8))
try write("assets/icons/app-icon-macos.svg", Data(tileSVG(.macOS).utf8))
try write("assets/icons/icon.png", pngData(render(.fullBleed, size: 1024)))
try write("assets/icons/icon.ico", icoData([16, 20, 24, 32, 40, 48, 64, 128, 256]))
try write("assets/logo.png", pngData(render(.macOS, size: 512)))
try write("src/MySSH.Desktop/Assets/icon.png", pngData(render(.fullBleed, size: 256)))
try write("src/MySSH.Desktop/Assets/icon-macos.png", pngData(render(.macOS, size: 512)))

// .icns via iconutil from a temporary .iconset
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("MySSH-\(UUID().uuidString).iconset")
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: iconset) }
for base in [16, 32, 128, 256, 512] {
    try pngData(render(.macOS, size: base)).write(to: iconset.appendingPathComponent("icon_\(base)x\(base).png"))
    try pngData(render(.macOS, size: base * 2)).write(to: iconset.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
let icns = root.appendingPathComponent("assets/icons/icon.icns")
let p = Process()
p.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
p.arguments = ["-c", "icns", iconset.path, "-o", icns.path]
try p.run()
p.waitUntilExit()
precondition(p.terminationStatus == 0, "iconutil failed")
print("wrote assets/icons/icon.icns (\((try? Data(contentsOf: icns).count) ?? 0) bytes)")
