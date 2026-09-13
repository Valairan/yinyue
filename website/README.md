# Yinyue landing page

A static one-page site with no build step. Copy this whole folder into the portfolio site
so that `valairan.tech/yinyue/` serves `index.html`. Every link inside is relative, so it
works from any sub-path.

## Placeholders to fill in

- **Background video** — `media/demo.mp4` (and optionally `media/demo.webm`). A screen
  recording of the app in use, looped. Keep it short and small: 15–30 s, 1080p or 1440p,
  no audio track (the video is muted anyway). Until the file exists the page shows
  `media/poster-applet.png`, a real render of the applet, so nothing looks broken.
- **Screenshots** — `media/screenshot-*.png` are real captures of the app driven with sample
  tracks and rendered at 2x by a small harness (a console project referencing `win/Yinyue.csproj`
  that shows the overlay, plays a fake track, opens each panel and composites the windows onto
  a dark canvas). Regenerate them when the UI changes; the `<figcaption>`s say what each shows.

## Two placeholders that need a URL or a file

- **Buy Me a Coffee** — the QR code in the Support section is `assets/buymeacoffee.png`
  (resized to 720px for the web). Add your page's URL as a link around the image so people at
  a desktop can click instead of scan.
- **macOS download** — both "Download for macOS" buttons are inert (`button--soon`, no
  `href`). When the Mac build ships, add the `href`, drop the `button--soon` class and the
  `soon` tag, and fill in the macOS facts.

## Releasing a new version

1. Build the MSI: `.\installer\build.ps1 -Version X.Y.Z` in the repo root.
2. Copy `installer\out\Yinyue-X.Y.Z.msi` into `downloads/` and remove the old one.
3. In `index.html`, update the two download links, the version text in the hero, the size,
   and the SHA-256 (`Get-FileHash downloads\Yinyue-X.Y.Z.msi -Algorithm SHA256`).

## Credits

Icons are [Lucide](https://lucide.dev) (ISC, licence in `assets/icons/`). Colours are
Catppuccin Mocha, the app's own palette.
