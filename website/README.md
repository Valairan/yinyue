# Yinyue landing page

A static one-page site with no build step. Copy this whole folder into the portfolio site
so that `valairan.tech/yinyue/` serves `index.html`. Every link inside is relative, so it
works from any sub-path.

## Placeholders to fill in

- **Background loop** — `media/demo.gif`, a 15 s recording of the main flows made by the same
  harness as the screenshots (its `--gif` mode drives the real UI and writes frames;
  `assemble_gif.py` folds still frames and builds the GIF with one shared palette). Regenerate
  it when the UI changes. `media/poster-applet.png` is shown instead for reduced-motion users.
- **Screenshots** — `media/screenshot-*.png` are real captures of the app driven with sample
  tracks and rendered at 2x by a small harness (a console project referencing `win/Yinyue.csproj`
  that shows the overlay, plays a fake track, opens each panel and composites the windows onto
  a dark canvas). Regenerate them when the UI changes; the `<figcaption>`s say what each shows.

## Placeholders

- **Buy Me a Coffee** — the Support card links to buymeacoffee.com/valairan as a whole; the QR
  code inside it is `assets/buymeacoffee.png`, resized to 720px for the web.
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
