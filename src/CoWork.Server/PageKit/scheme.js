// CoWork (brief 12): this page follows the theme chosen in CoWork. The canvas puts #scheme=dark or #scheme=light in the page's
// address; this turns the page's own prefers-color-scheme rules on or off to match, and again when it changes. Opened on its own,
// without that, the page follows the computer as usual.
(() => {
  const feature = /\(\s*prefers-color-scheme\s*:\s*(dark|light)\s*\)/gi;
  const original = new WeakMap();
  let both = null;
  const wanted = () => (/(?:^#|&)scheme=(dark|light)(?:&|$)/.exec(location.hash) || [])[1] || null;
  const swap = (text, want) => want ? text.replace(feature, (_, s) => s.toLowerCase() === want ? '(width >= 0px)' : '(width < 0px)') : text;
  const rewrite = (media, want) => {
    if (!media) return;
    let text = original.get(media);
    if (text === undefined) {
      text = media.mediaText;
      if (!/prefers-color-scheme/i.test(text)) return;
      original.set(media, text);
    }
    const next = swap(text, want);
    if (media.mediaText !== next) media.mediaText = next;
  };
  const walk = (rules, want) => {
    for (const r of rules) {
      if (r.media) rewrite(r.media, want);
      if (r.styleSheet) { try { walk(r.styleSheet.cssRules, want); } catch (e) {} }
      if (r.cssRules) walk(r.cssRules, want);
    }
  };
  const apply = () => {
    const want = wanted();
    const root = document.documentElement;
    if (both === null) { const cs = getComputedStyle(root).colorScheme || ''; both = /light/.test(cs) && /dark/.test(cs); }
    for (const sheet of document.styleSheets) {
      rewrite(sheet.media, want);
      try { walk(sheet.cssRules, want); } catch (e) {}
    }
    for (const el of document.querySelectorAll('source[media]')) {
      if (el.dataset.coworkMedia === undefined) {
        if (!/prefers-color-scheme/i.test(el.media)) continue;
        el.dataset.coworkMedia = el.media;
      }
      const next = swap(el.dataset.coworkMedia, want);
      if (el.media !== next) el.media = next;
    }
    if (both) root.style.colorScheme = want || '';
  };
  apply();
  document.addEventListener('DOMContentLoaded', apply);
  addEventListener('load', apply);
  addEventListener('hashchange', apply);
})();
