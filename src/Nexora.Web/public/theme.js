// Apply the saved theme before the page paints; keep working when storage is blocked.
(function () {
  var theme;
  try { theme = localStorage.getItem('nexora-theme'); } catch (_) { /* use system preference */ }
  if (theme !== 'light' && theme !== 'dark') {
    theme = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  }
  document.documentElement.dataset.theme = theme;
})();
