# Yinyue landing page

A static one-page site with no build step. Copy this whole folder into the portfolio site
so that `valairan.tech/yinyue/` serves `index.html`. Every link inside is relative, so it
works from any sub-path.

## Placeholders to fill in

- **Background video** — `media/demo.mp4` (and optionally `media/demo.webm`). A screen
  recording of the app in use, looped. Keep it short and small: 15–30 s, 1080p or 1440p,
  no audio track (the video is muted anyway). Until the file exists the page shows
  `media/poster-applet.png`, a real render of the applet, so nothing looks broken.
- **Screenshots** — the four dashed boxes in the "What it looks like" section. Replace each
  `<div class="shot__placeholder">` with `<img class="shot__placeholder" src="…" alt="…">`
  (the class carries the sizing). The captions say what to capture.

## Releasing a new version

1. Build the MSI: `.\installer\build.ps1 -Version X.Y.Z` in the repo root.
2. Copy `installer\out\Yinyue-X.Y.Z.msi` into `downloads/` and remove the old one.
3. In `index.html`, update the two download links, the version text in the hero, the size,
   and the SHA-256 (`Get-FileHash downloads\Yinyue-X.Y.Z.msi -Algorithm SHA256`).

## Credits

Icons are [Lucide](https://lucide.dev) (ISC, licence in `assets/icons/`). Colours are
Catppuccin Mocha, the app's own palette.
