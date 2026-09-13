// The page works with no JavaScript at all. The macOS processor toggle is CSS state on two
// radios at the top of <body>; this only remembers the choice between visits.
(function () {
  var arm64 = document.getElementById("mac-arm64");
  var x64 = document.getElementById("mac-x64");
  if (!arm64 || !x64) return;

  var KEY = "yinyue-mac-arch";
  try { if (localStorage.getItem(KEY) === "x64") x64.checked = true; } catch (e) { /* storage refused: fine */ }

  [arm64, x64].forEach(function (radio) {
    radio.addEventListener("change", function () {
      try { localStorage.setItem(KEY, x64.checked ? "x64" : "arm64"); } catch (e) { /* ignore */ }
    });
  });
})();
