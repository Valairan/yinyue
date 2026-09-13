// The page works with no JavaScript at all. This only tidies two edges of the video.
(function () {
  var video = document.querySelector(".backdrop__video");
  if (!video) return;

  // If no recording is present (or it cannot be decoded), leave the poster showing rather
  // than a black rectangle: the poster is a real screenshot of the app.
  video.addEventListener("error", function () { video.removeAttribute("src"); }, true);

  // Browsers sometimes refuse autoplay until the page is interacted with. Try once more on
  // the first interaction; still muted, so it is allowed.
  function nudge() {
    var p = video.play();
    if (p && p.catch) p.catch(function () {});
    window.removeEventListener("pointerdown", nudge);
    window.removeEventListener("keydown", nudge);
  }
  video.addEventListener("suspend", function () {
    if (video.paused) {
      window.addEventListener("pointerdown", nudge, { once: true });
      window.addEventListener("keydown", nudge, { once: true });
    }
  });
})();
