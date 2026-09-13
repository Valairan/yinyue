// The page works with no JavaScript at all: every download link has a real href. This only
// drives the macOS processor toggle — two DMGs, one per architecture — and remembers the
// choice, so both "Download for macOS" buttons on the page hand out the same file.
(function () {
  var card = document.querySelector("[data-mac-files]");
  if (!card) return;

  var files = {
    arm64: card.getAttribute("data-file-arm64"),
    x64: card.getAttribute("data-file-x64"),
  };
  var names = { arm64: "Apple silicon", x64: "Intel" };
  var KEY = "yinyue-mac-arch";

  function apply(arch, remember) {
    if (!files[arch]) return;
    document.querySelectorAll("[data-mac-download]").forEach(function (a) {
      a.setAttribute("href", files[arch]);
      a.setAttribute("title", "Yinyue for macOS, " + names[arch]);
    });
    document.querySelectorAll("[data-mac-arch]").forEach(function (b) {
      b.setAttribute("aria-pressed", String(b.getAttribute("data-mac-arch") === arch));
    });
    document.querySelectorAll("[data-mac-arch-name]").forEach(function (el) {
      el.textContent = names[arch];
    });
    if (remember) {
      try { localStorage.setItem(KEY, arch); } catch (e) { /* private mode: fine */ }
    }
  }

  document.querySelectorAll("[data-mac-arch]").forEach(function (b) {
    b.addEventListener("click", function () { apply(b.getAttribute("data-mac-arch"), true); });
  });

  // Apple silicon by default: it is what every Mac sold since 2020 has, and browsers do not
  // reliably reveal the processor, so guessing harder would only guess wrong quietly.
  var saved = null;
  try { saved = localStorage.getItem(KEY); } catch (e) { /* ignore */ }
  apply(saved === "x64" ? "x64" : "arm64", false);
})();
