// Builds the macOS assets from the SVG masters in this folder.
//
//     swift Common/make-mac-assets.swift
//
// Swift rather than Python, and AppKit rather than ImageMagick: NSImage reads SVG natively on
// macOS, so this needs nothing installed beyond the Command Line Tools the app already
// requires. The Windows generator needs ImageMagick with librsvg, which is the right answer
// there and an unnecessary dependency here.
//
// Everything comes from Logo.svg (the full 音乐 mark) and ShortLogo.svg (音 alone). Do not
// edit the outputs by hand.
//
// The size rule is the Windows one, and it matters more here. The full mark is 音 over 乐,
// more than twice as tall as it is wide — so in the 18 pt menu bar each character would get
// nine pixels and read as a smudge. Frames of 24 px and under carry the short mark, which is
// roughly square and gives every stroke twice the pixels; the full mark returns from 32 px up.
//
// Outputs:
//   mac/Assets/menubar.png, menubar@2x.png   the short mark, as a template image
//   mac/Assets/Yinyue.icns                   the app icon, mark on a rounded #1E1E2E plate
//   mac/Assets/placeholder.png               album-art fallback, shared with Windows

import AppKit
import Foundation

let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let common = root.appendingPathComponent("Common")
let out = root.appendingPathComponent("mac/Assets")

let fullMaster = common.appendingPathComponent("Logo.svg")
let shortMaster = common.appendingPathComponent("ShortLogo.svg")

/// Frames at or under this carry the short mark. Matches SHORT_UP_TO in make-win-assets.py.
let shortUpTo = 24

let plate = NSColor(srgbRed: 0x1E / 255.0, green: 0x1E / 255.0, blue: 0x2E / 255.0, alpha: 1)

try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func master(for size: Int) -> URL { size <= shortUpTo ? shortMaster : fullMaster }

/// A bitmap of exactly `size` × `size` PIXELS.
///
/// NSImage.lockFocus draws at the screen's backing scale, so on a Retina Mac an 18-point
/// canvas becomes a 36-pixel file — which silently made every asset twice its intended size.
/// An explicit representation pins the pixel dimensions regardless of the display.
func bitmap(_ size: Int, _ draw: (CGFloat) -> Void) -> NSBitmapImageRep {
    guard let rep = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else {
        FileHandle.standardError.write("could not make a \(size)px canvas\n".data(using: .utf8)!)
        exit(1)
    }

    rep.size = NSSize(width: size, height: size)

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    draw(CGFloat(size))
    NSGraphicsContext.restoreGraphicsState()

    return rep
}

/// The mark fitted inside `fraction` of a size×size transparent square, centred.
func raster(_ svg: URL, size: Int, fraction: Double, tint: NSColor?) -> NSBitmapImageRep {
    guard let source = NSImage(contentsOf: svg) else {
        FileHandle.standardError.write("cannot read \(svg.lastPathComponent)\n".data(using: .utf8)!)
        exit(1)
    }

    return bitmap(size) { side in
        let inner = side * CGFloat(fraction)

        // Fit rather than fill, so a tall mark keeps its proportions instead of being cropped.
        let scale = min(inner / source.size.width, inner / source.size.height)
        let drawn = NSSize(width: source.size.width * scale, height: source.size.height * scale)
        let origin = NSPoint(x: (side - drawn.width) / 2, y: (side - drawn.height) / 2)

        source.draw(in: NSRect(origin: origin, size: drawn))

        // The masters fill white. Recolouring is a source-atop pass rather than a rewrite of
        // the SVG, which is what the Windows script has to do — AppKit can composite over the
        // alpha it just drew.
        if let tint {
            tint.set()
            NSRect(x: 0, y: 0, width: side, height: side).fill(using: .sourceAtop)
        }
    }
}

func png(_ rep: NSBitmapImageRep) -> Data {
    guard let data = rep.representation(using: .png, properties: [:]) else {
        FileHandle.standardError.write("could not encode a PNG\n".data(using: .utf8)!)
        exit(1)
    }
    return data
}

/// The mark on a rounded plate — a bare transparent glyph disappears against a matching
/// Finder background.
func plated(size: Int) -> NSBitmapImageRep {
    let glyph = raster(master(for: size), size: size,
                       fraction: size <= shortUpTo ? 0.66 : 0.74, tint: nil)

    return bitmap(size) { side in
        plate.set()
        NSBezierPath(roundedRect: NSRect(x: 0, y: 0, width: side, height: side),
                     xRadius: side * 0.22, yRadius: side * 0.22).fill()

        glyph.draw(in: NSRect(x: 0, y: 0, width: side, height: side))
    }
}

// --- Menu-bar mark ------------------------------------------------------------------
//
// ONE asset, not the two Windows needs. A template image is a mask: AppKit reads only its
// alpha and draws it in whatever colour the menu bar requires, inverting itself between light
// and dark. So there is no menubar-light/menubar-dark pair and nothing watches for a theme
// change — which is App.ApplyTrayIcon's entire job on Windows.
//
// The short mark at both sizes: 18 pt is well under the threshold, and @2x is the same mark
// at twice the resolution rather than a different one.
for (name, size) in [("menubar.png", 18), ("menubar@2x.png", 36)] {
    let image = raster(shortMaster, size: size, fraction: 0.86, tint: nil)
    try png(image).write(to: out.appendingPathComponent(name))
}

// --- Application icon ---------------------------------------------------------------
let iconset = out.appendingPathComponent("Yinyue.iconset")
try? FileManager.default.removeItem(at: iconset)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

for size in [16, 32, 128, 256, 512] {
    try png(plated(size: size)).write(to: iconset.appendingPathComponent("icon_\(size)x\(size).png"))
    try png(plated(size: size * 2)).write(to: iconset.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path,
                      "-o", out.appendingPathComponent("Yinyue.icns").path]
try iconutil.run()
iconutil.waitUntilExit()
try? FileManager.default.removeItem(at: iconset)

// --- Album-art fallback -------------------------------------------------------------
//
// Copied rather than generated: a finished asset shared with the Windows app, not a
// derivative of the logo masters.
let placeholder = out.appendingPathComponent("placeholder.png")
try? FileManager.default.removeItem(at: placeholder)
try FileManager.default.copyItem(at: root.appendingPathComponent("win/Resources/placeholder.png"),
                                 to: placeholder)

print("wrote menubar.png, menubar@2x.png, Yinyue.icns, placeholder.png")
