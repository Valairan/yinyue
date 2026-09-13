"""
Builds the Windows icons and the website marks from the SVG masters in this folder.

    python Common/make-win-assets.py

Needs ImageMagick 7 (`magick`) with librsvg, which is what the Windows build of ImageMagick
ships. Everything is regenerated from Logo.svg (the full 音乐 mark) and ShortLogo.svg (音
alone); do not edit the .ico files by hand.

Why two masters: the full mark is 音 over 乐, more than twice as tall as it is wide, and at
the tray's 16 px each character gets seven pixels — no stroke survives that, vector or not.
So every frame of 24 px and under carries 音 alone, which is roughly square and gives each
stroke twice the pixels, and the full mark comes back from 32 px up. Windows picks the exact
frame for the size it needs, so both live in the one .ico.

Outputs:
  win/Assets/tray-light.ico   white mark, transparent — for a dark taskbar
  win/Assets/tray-dark.ico    dark mark, transparent — for a light taskbar
  win/Assets/yinyue.ico       white mark on a rounded #1E1E2E plate — the application icon
  website/assets/mark.svg, mark-short.svg, favicon.svg
"""
import os
import re
import shutil
import subprocess
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
FULL = os.path.join(HERE, "Logo.svg")
SHORT = os.path.join(HERE, "ShortLogo.svg")
ASSETS = os.path.join(ROOT, "win", "Assets")
SITE = os.path.join(ROOT, "website", "assets")

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]
SHORT_UP_TO = 24                 # frames at or under this size carry the short mark
PLATE = "#1E1E2E"
DARK_MARK = "#11111B"


def run(*args):
    subprocess.run(["magick", *args], check=True)


def recoloured(svg_path, colour, tmp):
    """The masters fill #fff through a CSS class; rewrite that for the dark variant."""
    text = open(svg_path, encoding="utf-8").read()
    text = re.sub(r"fill:\s*#fff;", f"fill: {colour};", text)
    out = os.path.join(tmp, f"{os.path.basename(svg_path)}-{colour.strip('#')}.svg")
    open(out, "w", encoding="utf-8").write(text)
    return out


def raster(svg, size, fraction, tmp, name):
    """The mark fitted inside `fraction` of a size×size transparent square, centred."""
    inner = max(1, round(size * fraction))
    out = os.path.join(tmp, f"{name}-{size}.png")
    run("-background", "none", "-density", "600", svg,
        "-resize", f"{inner}x{inner}", "-gravity", "center", "-extent", f"{size}x{size}",
        "-define", "png:color-type=6", out)
    return out


def plate(size, tmp):
    """A rounded dark square, radius about a fifth of the side, like a Windows app tile."""
    out = os.path.join(tmp, f"plate-{size}.png")
    r = max(2, round(size * 0.22))
    run("-size", f"{size}x{size}", "xc:none", "-fill", PLATE,
        "-draw", f"roundrectangle 0,0 {size - 1},{size - 1} {r},{r}", out)
    return out


def build_ico(frames, out):
    run(*frames, out)
    print(f"  {os.path.relpath(out, ROOT)}  ({len(frames)} frames)")


def main():
    os.makedirs(ASSETS, exist_ok=True)
    os.makedirs(SITE, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        dark_full = recoloured(FULL, DARK_MARK, tmp)
        dark_short = recoloured(SHORT, DARK_MARK, tmp)

        def mark_for(size, light):
            if size <= SHORT_UP_TO:
                return (SHORT if light else dark_short), 0.86
            return (FULL if light else dark_full), 0.92

        # Tray icons: the bare mark, no plate, so it sits on the taskbar like the system's own.
        light, dark = [], []
        for size in SIZES:
            svg, frac = mark_for(size, light=True)
            light.append(raster(svg, size, frac, tmp, "light"))
            svg, frac = mark_for(size, light=False)
            dark.append(raster(svg, size, frac, tmp, "dark"))
        build_ico(light, os.path.join(ASSETS, "tray-light.ico"))
        build_ico(dark, os.path.join(ASSETS, "tray-dark.ico"))

        # Application icon: the white mark on a plate, because a bare transparent glyph
        # disappears against a matching Explorer background.
        app = []
        for size in SIZES:
            svg, _ = mark_for(size, light=True)
            frac = 0.66 if size <= SHORT_UP_TO else 0.74
            glyph = raster(svg, size, frac, tmp, "app")
            out = os.path.join(tmp, f"tile-{size}.png")
            run(plate(size, tmp), glyph, "-gravity", "center", "-composite", out)
            app.append(out)
        build_ico(app, os.path.join(ASSETS, "yinyue.ico"))

    # The website draws the marks as vectors.
    shutil.copyfile(FULL, os.path.join(SITE, "mark.svg"))
    shutil.copyfile(SHORT, os.path.join(SITE, "mark-short.svg"))

    # Favicon: the short mark on the plate, as one self-contained SVG. Browser tabs are
    # often light, so the plate is what keeps a white mark visible there.
    short = open(SHORT, encoding="utf-8").read()
    view = re.search(r'viewBox="0 0 ([\d.]+) ([\d.]+)"', short)
    w, h = float(view.group(1)), float(view.group(2))
    path = re.search(r'<path[^>]*\bd="([^"]+)"', short).group(1)
    box = 256.0
    scale = (box * 0.66) / max(w, h)
    tx = (box - w * scale) / 2
    ty = (box - h * scale) / 2
    favicon = f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {box:g} {box:g}">
  <!-- Generated by Common/make-win-assets.py from ShortLogo.svg. -->
  <rect width="{box:g}" height="{box:g}" rx="{box * 0.22:g}" fill="{PLATE}"/>
  <path transform="translate({tx:.2f} {ty:.2f}) scale({scale:.5f})" fill="#fff" d="{path}"/>
</svg>
"""
    open(os.path.join(SITE, "favicon.svg"), "w", encoding="utf-8").write(favicon)
    print("  website/assets/mark.svg, mark-short.svg, favicon.svg")


if __name__ == "__main__":
    main()
