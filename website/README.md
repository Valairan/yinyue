# Yinyue landing page

A static one-page site with no build step. Copy this whole folder into the portfolio site
so that `valairan.tech/yinyue/` serves `index.html`. Every link inside is relative, so it
works from any sub-path.

## What is generated, and how

- **Background loop** — `media/demo.gif`, a 15 s recording of the main flows made by the same
  harness as the screenshots (its `--gif` mode drives the real UI and writes frames;
  `assemble_gif.py` folds still frames and builds the GIF with one shared palette). Regenerate
  it when the UI changes. `media/poster-applet.png` is shown instead for reduced-motion users.
- **Screenshots** — `media/screenshot-*.png` are real captures of the app driven with sample
  tracks and rendered at 2x by a small harness (a console project referencing `win/Yinyue.csproj`
  that shows the overlay, plays a fake track, opens each panel and composites the windows onto
  a dark canvas). Regenerate them when the UI changes; the `<figcaption>`s say what each shows.
- **Marks and favicon** — `assets/mark.svg`, `mark-short.svg` and `favicon.svg` come from
  `Common/make-win-assets.py`, which also builds the Windows icons.
- **Buy Me a Coffee** — the Support card links to buymeacoffee.com/valairan as a whole; the QR
  code inside it is `assets/buymeacoffee.png`, resized to 720px for the web.

## Downloads

`downloads/` holds three files per release:

| File | For |
|---|---|
| `Yinyue-X.Y.Z.msi` | Windows 10 and 11 |
| `Yinyue-X.Y.Z-arm64.dmg` | macOS on Apple silicon (M1 and later) |
| `Yinyue-X.Y.Z-x64.dmg` | macOS on Intel |

The macOS card has a processor toggle beside its download button. It is CSS, not script: two
radios at the top of `index.html` (`#mac-arm64`, checked by default, and `#mac-x64`) and a
`~ main` sibling selector show whichever links and toggle label carry the matching
`mac-only-*` class. Each toggle label is a `<label for>` the *other* radio, which is what makes
a click flip it. `script.js` only remembers the choice between visits, so the toggle works even
where scripts are stripped — a portfolio that injects this page's HTML, for instance.

The MSI is gitignored and copied in from the Windows machine at release time. The DMGs are
committed, so a clone carries them; mind the size, since git keeps every version forever and
GitHub refuses single files over 100 MB.

## Releasing a new version

**Windows**, from the repo root:

1. Build the MSI: `.\installer\build.ps1 -Version X.Y.Z`.
2. Copy `installer\out\Yinyue-X.Y.Z.msi` into `downloads/` and remove the old one.
3. In `index.html`, update the Windows links, the version text in the hero and the card, the
   size, and the SHA-256 (`Get-FileHash downloads\Yinyue-X.Y.Z.msi -Algorithm SHA256`).

**macOS**, on a Mac with Xcode and the .NET 8 SDK:

1. `./installer/build-mac.sh X.Y.Z` — writes `Yinyue-X.Y.Z-arm64.dmg` and
   `Yinyue-X.Y.Z-x64.dmg` into `downloads/` and prints each size and SHA-256.
   `./installer/build-mac.sh X.Y.Z universal` builds one bundle carrying both instead, which
   is the sum of the two rather than a saving.
2. In `index.html`, update the four macOS links (two per processor: hero and card), the
   version text, and the two hashes in the SHA-256 row. The DMGs are separate, so a single link cannot serve both:
   an Intel visitor given the arm64 file gets an app that will not open, which is what the
   processor toggle is for.

The DMGs are **ad-hoc signed** unless `DEVELOPER_ID` is set, which means Gatekeeper refuses a
downloaded copy until the user right-clicks and chooses Open. That only affects the first
launch, and the macOS card says so while it is true. With an Apple Developer Program membership:

    DEVELOPER_ID="Developer ID Application: Your Name (TEAMID)" \
    NOTARY_PROFILE=yinyue ./installer/build-mac.sh X.Y.Z

## Credits

Icons are [Lucide](https://lucide.dev) (ISC, licence in `assets/icons/`). Colours are
Catppuccin Mocha, the app's own palette.
