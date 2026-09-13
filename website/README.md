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
- **macOS facts** — both "Download for macOS" buttons now link to the DMG. The macOS card
  still has no `Requires` / `Installer` / `SHA-256` / signing rows the way the Windows one
  does, and three lines still describe macOS as unreleased: the hero meta ("macOS next"), the
  card's `platform--soon` class, and its "In development" line.

## Releasing a new version

The installers are **not committed** — `downloads/` is gitignored for both. Build them at
release time and copy this whole folder to the host; a 43 MB DMG in git history would outweigh
the rest of the repository several times over and could only be removed by rewriting it.

**Windows**, from the repo root:

1. Build the MSI: `.\installer\build.ps1 -Version X.Y.Z`.
2. Copy `installer\out\Yinyue-X.Y.Z.msi` into `downloads/` and remove the old one.
3. In `index.html`, update the two download links, the version text in the hero, the size,
   and the SHA-256 (`Get-FileHash downloads\Yinyue-X.Y.Z.msi -Algorithm SHA256`).

**macOS**, on a Mac with Xcode and the .NET 8 SDK:

1. `./installer/build-mac.sh X.Y.Z` — it writes straight into `downloads/` and prints the
   size and SHA-256.
2. Update the two macOS links and facts in `index.html` to match.

The DMG is **ad-hoc signed** unless `DEVELOPER_ID` is set, which means Gatekeeper refuses a
downloaded copy until the user right-clicks and chooses Open. That only affects the first
launch — start at sign-in and everything else work normally afterwards — but the macOS card
should say so while it is true. With an Apple Developer Program membership:

    DEVELOPER_ID="Developer ID Application: Your Name (TEAMID)" \
    NOTARY_PROFILE=yinyue ./installer/build-mac.sh X.Y.Z

## Credits

Icons are [Lucide](https://lucide.dev) (ISC, licence in `assets/icons/`). Colours are
Catppuccin Mocha, the app's own palette.
