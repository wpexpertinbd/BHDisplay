// Renders BHDisplay's app icon (Big Sur grid: 824pt body inside 1024 canvas) → AppIcon.iconset
import AppKit

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    let s = CGFloat(px) / 1024
    ctx.scaleBy(x: s, y: s)

    // Tile with soft drop shadow
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let tile = NSBezierPath(roundedRect: body, xRadius: 185, yRadius: 185)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: NSColor.black.withAlphaComponent(0.28).cgColor)
    NSColor(red: 0.10, green: 0.19, blue: 0.42, alpha: 1).setFill(); tile.fill()
    ctx.restoreGState()
    ctx.saveGState(); tile.addClip()
    NSGradient(colors: [NSColor(red: 0.27, green: 0.47, blue: 0.95, alpha: 1),
                        NSColor(red: 0.11, green: 0.20, blue: 0.48, alpha: 1)])!.draw(in: body, angle: -90)
    // subtle top sheen
    NSGradient(colors: [NSColor.white.withAlphaComponent(0.18), NSColor.white.withAlphaComponent(0)])!
        .draw(in: CGRect(x: 100, y: 560, width: 824, height: 364), angle: -90)
    ctx.restoreGState()

    // Monitor: bezel, screen, stand
    let bezel = NSBezierPath(roundedRect: CGRect(x: 222, y: 360, width: 580, height: 380), xRadius: 44, yRadius: 44)
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -10), blur: 24, color: NSColor.black.withAlphaComponent(0.35).cgColor)
    NSColor.white.setFill(); bezel.fill()
    ctx.restoreGState()
    let screen = CGRect(x: 252, y: 392, width: 520, height: 318)
    let screenPath = NSBezierPath(roundedRect: screen, xRadius: 20, yRadius: 20)
    ctx.saveGState(); screenPath.addClip()
    NSGradient(colors: [NSColor(red: 0.09, green: 0.13, blue: 0.27, alpha: 1),
                        NSColor(red: 0.16, green: 0.28, blue: 0.60, alpha: 1)])!.draw(in: screen, angle: -60)
    ctx.restoreGState()
    let neck = NSBezierPath(roundedRect: CGRect(x: 472, y: 262, width: 80, height: 104), xRadius: 10, yRadius: 10)
    let foot = NSBezierPath(roundedRect: CGRect(x: 372, y: 232, width: 280, height: 44), xRadius: 22, yRadius: 22)
    NSColor(white: 0.93, alpha: 1).setFill(); neck.fill(); foot.fill()

    // Switch arrows on the screen
    let cfg = NSImage.SymbolConfiguration(pointSize: 220, weight: .heavy)
        .applying(.init(paletteColors: [NSColor(red: 0.55, green: 0.85, blue: 1.0, alpha: 1)]))
    if let sym = NSImage(systemSymbolName: "arrow.left.arrow.right", accessibilityDescription: nil)?.withSymbolConfiguration(cfg) {
        let sz = sym.size, scale = min(400 / sz.width, 250 / sz.height)
        let r = CGRect(x: screen.midX - sz.width * scale / 2, y: screen.midY - sz.height * scale / 2,
                       width: sz.width * scale, height: sz.height * scale)
        sym.draw(in: r)
    }
    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

guard CommandLine.arguments.count == 2 else {
    FileHandle.standardError.write("usage: swift make-icon.swift <out>/AppIcon.iconset   (also writes <out>/AppIcon-1024.png)\n".data(using: .utf8)!)
    exit(2)
}
let dir = URL(fileURLWithPath: CommandLine.arguments[1])
do { try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true) }
catch { FileHandle.standardError.write("cannot create \(dir.path): \(error)\n".data(using: .utf8)!); exit(1) }
func save(_ data: Data, _ url: URL) {
    do { try data.write(to: url) }
    catch { FileHandle.standardError.write("cannot write \(url.path): \(error)\n".data(using: .utf8)!); exit(1) }
}
for base in [16, 32, 128, 256, 512] {
    save(render(base), dir.appendingPathComponent("icon_\(base)x\(base).png"))
    save(render(base * 2), dir.appendingPathComponent("icon_\(base)x\(base)@2x.png"))
}
save(render(1024), dir.deletingLastPathComponent().appendingPathComponent("AppIcon-1024.png"))
