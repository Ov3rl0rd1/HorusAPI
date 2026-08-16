export function byId(id) { return document.getElementById(id); }
export function qsa(selector, root = document) { return Array.from(root.querySelectorAll(selector)); }

export function detectOS() {
  try {
    const ua = navigator.userAgent || '';
    if (/android/i.test(ua)) return 'android';
    if (/iphone|ipad|ipod/i.test(ua)) return 'ios';
    if (/macintosh|mac os/i.test(ua)) return 'mac';
    if (/win/i.test(ua)) return 'windows';
  } catch (e) {}
  return 'other';
}

export function formatSize(bytes) {
  if (!bytes || bytes < 0) return '';
  const mb = bytes / 1048576;
  if (mb >= 1024) return (mb / 1024).toFixed(1).replace('.', ',') + ' ГБ';
  if (mb >= 10) return Math.round(mb) + ' МБ';
  if (mb >= 1) return mb.toFixed(1).replace('.', ',') + ' МБ';
  return Math.max(1, Math.round(bytes / 1024)) + ' КБ';
}

export function prefersReducedMotion() {
  return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
}

export function queryParam(name) {
  try { return new URLSearchParams(location.search).get(name) || ''; } catch (e) { return ''; }
}
