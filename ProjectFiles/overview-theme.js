(function () {
  var source = window.location.search + "&" + window.location.hash.replace("#", "");
  var match = /theme=(light|dark)/i.exec(source);
  var theme = match ? match[1].toLowerCase()
    : (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
  document.documentElement.setAttribute("data-theme", theme);
})();
