import { byId, detectOS, formatSize } from './util.js';
import { initParallax, initReveal } from './motion.js';

// Файлы отдаёт наш же nginx: /download/… — зеркало последнего релиза
// (nginx/sync-releases.sh). Ссылки статичные и версии не содержат, поэтому
// работают и без JS; /download/latest.json нужен только ради версии и
// размеров. key совпадает с ключами в манифесте.
const FILES = [
  { key: 'win-msi',        group: 'windows', href: '/download/Horus-win-x64.msi',           name: 'Установщик · .msi',         hint: 'Windows 10 и 11 · x64' },
  { key: 'win-zip',        group: 'windows', href: '/download/Horus-win-x64-portable.zip',  name: 'Портативная версия · .zip', hint: 'Без установки · x64' },
  { key: 'android-arm64',  group: 'android', href: '/download/Horus-android-arm64-v8a.apk', name: 'APK · arm64-v8a',           hint: 'Почти все телефоны' },
  { key: 'android-x86_64', group: 'android', href: '/download/Horus-android-x86_64.apk',    name: 'APK · x86_64',              hint: 'Эмуляторы и планшеты на x86' },
  { key: 'sha256sums',     group: 'checks',  href: '/download/SHA256SUMS.txt',              name: 'SHA256SUMS.txt',            hint: 'Контрольные суммы сборок' }
];
const PRIMARY = {
  'win-msi':       { label: 'Скачать для Windows', sub: 'Установщик · .msi' },
  'android-arm64': { label: 'Скачать для Android', sub: 'APK · arm64-v8a' }
};
const GROUPS = [
  { key: 'windows', title: 'WINDOWS' },
  { key: 'android', title: 'ANDROID' },
  { key: 'checks',  title: 'ПРОВЕРКА ФАЙЛОВ' }
];
const PRICE = { monthly: 199, three: 499 };
const MONTH_WORDS = ['1 месяц', '2 месяца', '3 месяца'];

const os = detectOS();
let release = null;
let months = 1;

// ── Какой макет показываем: ноутбук или телефон ───────────────────────────
const showPhone = os === 'android' || os === 'ios';
byId('mock-laptop').hidden = showPhone;
byId('mock-phone').hidden = !showPhone;
byId('side-phone').hidden = showPhone;

// ── Список файлов ─────────────────────────────────────────────────────────
// Пока манифест не пришёл — показываем весь список; после него оставляем
// только то, что реально лежит в зеркале.
function resolvedFiles() {
  const mirrored = {};
  if (release) release.files.forEach(function (f) { mirrored[f.key] = f; });
  return FILES
    .filter(function (f) { return !release || mirrored[f.key]; })
    .map(function (f) {
      const m = mirrored[f.key];
      return {
        key: f.key, group: f.group, name: f.name, hint: f.hint,
        href: (m && m.url) || f.href,
        size: m ? formatSize(m.size) : ''
      };
    });
}

function renderPrimary(files) {
  const key = os === 'android' ? 'android-arm64' : 'win-msi';
  const file = files.filter(function (f) { return f.key === key; })[0] || files[0];
  const link = byId('primary-download');
  if (!file) { link.hidden = true; return; }
  const meta = PRIMARY[file.key] || null;
  link.href = file.href;
  link.hidden = false;
  byId('primary-label').textContent = meta ? meta.label : 'Скачать приложение';
  byId('primary-sub').textContent = [meta ? meta.sub : file.name, file.size].filter(Boolean).join(' · ');
}

function renderFileList(files) {
  const host = byId('files-groups');
  host.textContent = '';
  GROUPS.forEach(function (group) {
    const items = files.filter(function (f) { return f.group === group.key; });
    if (!items.length) return;

    const wrap = document.createElement('div');
    wrap.className = 'files__group';
    const kind = document.createElement('div');
    kind.className = 'files__kind';
    kind.textContent = group.title;
    wrap.appendChild(kind);

    items.forEach(function (f) {
      const a = document.createElement('a');
      a.className = 'files__item';
      a.href = f.href;
      a.addEventListener('click', closeFiles);
      const text = document.createElement('span');
      text.className = 'files__text';
      const name = document.createElement('span');
      name.className = 'files__name';
      name.textContent = f.name;
      const hint = document.createElement('span');
      hint.className = 'files__hint';
      hint.textContent = f.hint;
      text.appendChild(name);
      text.appendChild(hint);
      const size = document.createElement('span');
      size.className = 'files__size';
      size.textContent = f.size;
      a.appendChild(text);
      a.appendChild(size);
      wrap.appendChild(a);
    });
    host.appendChild(wrap);
  });
  byId('files-version').textContent = release && release.version ? 'Версия ' + release.version : '';
}

function renderDownloads() {
  const files = resolvedFiles();
  renderPrimary(files);
  renderFileList(files);
}

// ── Открыть / закрыть панель ──────────────────────────────────────────────
const hero = byId('hero');
const panel = byId('files-panel');
const backdrop = byId('files-backdrop');
const filesToggle = byId('files-toggle');

// Кнопка стоит низко, поэтому место под панель ищем каждый раз: снизу от
// кнопки, сверху от неё или листом у нижней кромки экрана. Сверху свободно
// только до липкой шапки — от её нижней кромки и считаем.
function placePanel() {
  panel.classList.remove('files--up', 'files--sheet');
  panel.style.maxHeight = '';
  if (window.matchMedia('(max-width: 640px)').matches) return;

  const r = filesToggle.getBoundingClientRect();
  const vh = window.innerHeight || 800;
  const navBottom = byId('hero').ownerDocument.querySelector('.nav').getBoundingClientRect().bottom;
  const need = Math.min(panel.scrollHeight + 24, 420);
  const below = vh - r.bottom - 24;
  const above = r.top - navBottom - 12;

  if (below >= need) return;
  if (above >= need) { panel.classList.add('files--up'); return; }
  if (above > below && above >= 260) {
    panel.classList.add('files--up');
    panel.style.maxHeight = Math.round(above) + 'px';
    return;
  }
  panel.classList.add('files--sheet');
}

function setFilesOpen(open) {
  panel.hidden = !open;
  backdrop.hidden = !open;
  hero.classList.toggle('is-files-open', open);
  filesToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  if (open) { panel.scrollTop = 0; placePanel(); }
}
function closeFiles() { setFilesOpen(false); }

filesToggle.addEventListener('click', function () { setFilesOpen(panel.hidden); });
backdrop.addEventListener('click', closeFiles);
byId('files-close').addEventListener('click', closeFiles);
window.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeFiles(); });
// Панель привязана к кнопке — закрываем её, когда она уехала из видимости.
// Лист у нижней кромки закреплён на экране и при проматывании остаётся.
window.addEventListener('scroll', function () {
  if (panel.hidden) return;
  const r = panel.getBoundingClientRect();
  if (r.bottom < 0 || r.top > (window.innerHeight || 800)) closeFiles();
}, { passive: true });

window.addEventListener('resize', function () { if (!panel.hidden) placePanel(); });

// ── Цена ──────────────────────────────────────────────────────────────────
const slider = byId('months');
const ticks = Array.prototype.slice.call(document.querySelectorAll('.price__tick'));

function priceFor(m) {
  if (m <= 1) return PRICE.monthly;
  if (m >= 3) return PRICE.three;
  return Math.round(PRICE.monthly * 2 * 0.95);
}

function renderPrice() {
  const total = priceFor(months);
  const full = PRICE.monthly * months;
  const saveAmt = full - total;
  const savePct = full > 0 ? Math.round((saveAmt / full) * 100) : 0;

  slider.value = String(months);
  slider.style.setProperty('--fill', (((months - 1) / 2) * 100) + '%');
  ticks.forEach(function (b, i) { b.classList.toggle('is-on', i + 1 === months); });

  byId('price-total').textContent = total + ' ₽';
  byId('price-for').textContent = 'за ' + MONTH_WORDS[months - 1];
  byId('price-permonth').textContent = months > 1
    ? '≈ ' + Math.round(total / months) + ' ₽ в месяц'
    : 'все функции включены';

  const save = byId('price-save');
  const show = months > 1 && saveAmt > 0;
  save.hidden = !show;
  if (show) save.textContent = 'Выгода ' + savePct + '% · −' + saveAmt + ' ₽';
}

slider.addEventListener('input', function (e) { months = Number(e.target.value); renderPrice(); });
ticks.forEach(function (b, i) {
  b.addEventListener('click', function () { months = i + 1; renderPrice(); });
});

// ── Движение ──────────────────────────────────────────────────────────────
const device = byId('device');
const sidePhone = byId('side-phone');
const orbs = [
  { el: byId('orb-a'), k: -0.06 },
  { el: byId('orb-b'), k: 0.08 },
  { el: byId('orb-c'), k: -0.04 }
];

initParallax(function (pos, reduced) {
  if (reduced) {
    device.style.transform = 'none';
    sidePhone.style.opacity = '1';
    sidePhone.style.transform = 'none';
    return;
  }
  const vh = window.innerHeight || 800;
  const p = Math.max(0, Math.min(1, pos / (vh * 0.85)));
  device.style.transform =
    'rotateY(' + (-22 + p * 26).toFixed(2) + 'deg) rotateX(' + (7 - p * 9).toFixed(2) + 'deg)' +
    ' translateY(' + (p * 46).toFixed(1) + 'px) scale(' + (1 + p * 0.05).toFixed(3) + ')';
  sidePhone.style.opacity = String(Math.min(1, p * 1.7));
  sidePhone.style.transform =
    'translate3d(' + ((1 - p) * 90).toFixed(1) + 'px, ' + ((1 - p) * 50).toFixed(1) + 'px, 0)' +
    ' rotate(' + (8 - p * 4).toFixed(2) + 'deg)';
  orbs.forEach(function (o) { if (o.el) o.el.style.translate = '0 ' + (pos * o.k).toFixed(1) + 'px'; });
});

initReveal();

renderPrice();
renderDownloads();

// Манифест зеркала: версия и размеры. Не дошёл — кнопки всё равно рабочие.
fetch('/download/latest.json', { cache: 'no-store' })
  .then(function (r) { return r.ok ? r.json() : null; })
  .then(function (rel) {
    if (rel && Array.isArray(rel.files)) { release = rel; renderDownloads(); }
  })
  .catch(function () {});
