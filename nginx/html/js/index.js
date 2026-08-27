import { byId, detectOS, formatSize, plural } from './util.js';
import { billingPlans } from './endpoints.js';
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
const os = detectOS();
let release = null;

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
// только до липкой шапки: от её нижней кромки и считаем, а высоту всегда
// зажимаем по найденному месту — иначе длинный список уезжает под шапку.
const PANEL_GAP = 12;      // отступ от кнопки (совпадает с CSS)
const PANEL_EDGE = 20;     // запас до кромки экрана и до шапки
const PANEL_MIN = 240;     // ниже этого панель не сжимаем — показываем листом

function placePanel() {
  panel.classList.remove('files--up', 'files--sheet');
  panel.style.maxHeight = '';
  if (window.matchMedia('(max-width: 640px)').matches) return;

  const r = filesToggle.getBoundingClientRect();
  const vh = window.innerHeight || 800;
  const nav = document.querySelector('.nav');
  const navBottom = nav ? nav.getBoundingClientRect().bottom : 0;
  const need = panel.scrollHeight + 2;
  const below = vh - r.bottom - PANEL_GAP - PANEL_EDGE;
  const above = r.top - navBottom - PANEL_GAP - PANEL_EDGE;

  if (below >= need) return;

  if (above >= need || (above > below && above >= PANEL_MIN)) {
    panel.classList.add('files--up');
    panel.style.maxHeight = Math.round(Math.min(above, need)) + 'px';
    return;
  }
  if (below >= PANEL_MIN) {
    panel.style.maxHeight = Math.round(Math.min(below, need)) + 'px';
    return;
  }
  panel.classList.add('files--sheet');
}

// Пока панель открыта, страница под ней стоит на месте. Ширину исчезающего
// скроллбара возвращаем паддингом, иначе макет дёргается.
function lockScroll(on) {
  const root = document.documentElement;
  if (on) {
    const bar = window.innerWidth - root.clientWidth;
    root.style.paddingRight = bar > 0 ? bar + 'px' : '';
    root.classList.add('is-locked');
  } else {
    root.classList.remove('is-locked');
    root.style.paddingRight = '';
  }
}

function setFilesOpen(open) {
  panel.hidden = !open;
  backdrop.hidden = !open;
  hero.classList.toggle('is-files-open', open);
  filesToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
  if (open) { panel.scrollTop = 0; placePanel(); }
  lockScroll(open);
}
function closeFiles() { setFilesOpen(false); }

filesToggle.addEventListener('click', function () { setFilesOpen(panel.hidden); });
backdrop.addEventListener('click', closeFiles);
byId('files-close').addEventListener('click', closeFiles);
window.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeFiles(); });
// Прокрутка страницы при открытой панели заблокирована, но событие всё равно
// может прийти (якорь, клавиатура) — тогда закрываем, если панель ушла из вида.
window.addEventListener('scroll', function () {
  if (panel.hidden) return;
  const r = panel.getBoundingClientRect();
  if (r.bottom < 0 || r.top > (window.innerHeight || 800)) closeFiles();
}, { passive: true });

window.addEventListener('resize', function () { if (!panel.hidden) placePanel(); });

// ── Цена ──────────────────────────────────────────────────────────────────
// Сроки и суммы берём из /billing/plans. Эндпоинт анонимный, гостю отдаются
// только публичные тарифы; если запрос не удался — остаются встроенные значения
// (они же лежат в HTML, чтобы блок цены работал и без JS).
const FALLBACK_TIERS = [
  { label: '1 мес', full: 'за 1 месяц',  amount: 199, months: 1 },
  { label: '2 мес', full: 'за 2 месяца', amount: 378, months: 2 },
  { label: '3 мес', full: 'за 3 месяца', amount: 499, months: 3 }
];
const UNIT_SHORT = { day: ['дн', 'дн', 'дн'], week: ['нед', 'нед', 'нед'],
  month: ['мес', 'мес', 'мес'], year: ['год', 'года', 'лет'] };
const UNIT_FULL = { day: ['день', 'дня', 'дней'], week: ['неделю', 'недели', 'недель'],
  month: ['месяц', 'месяца', 'месяцев'], year: ['год', 'года', 'лет'] };
const IN_MONTHS = { day: 1 / 30, week: 7 / 30, month: 1, year: 12 };

let tiers = FALLBACK_TIERS;
let tier = 0;

const slider = byId('months');
const ticksHost = byId('price-ticks');
let ticks = [];

function money(value) { return Math.round(value).toLocaleString('ru-RU') + ' ₽'; }

function planToTier(p) {
  const n = Number(p.interval_count) || 1;
  const unit = UNIT_SHORT[p.interval_unit] ? p.interval_unit : 'month';
  return {
    label: n + ' ' + plural(n, UNIT_SHORT[unit]),
    full: 'за ' + n + ' ' + plural(n, UNIT_FULL[unit]),
    amount: Number(p.amount) || 0,
    months: n * IN_MONTHS[unit]
  };
}

function renderTicks() {
  ticksHost.textContent = '';
  ticks = tiers.map(function (t, i) {
    const b = document.createElement('button');
    b.className = 'price__tick' + (i === tier ? ' is-on' : '');
    b.type = 'button';
    b.textContent = t.label;
    b.addEventListener('click', function () { tier = i; renderPrice(); });
    ticksHost.appendChild(b);
    return b;
  });
  slider.min = '1';
  slider.max = String(Math.max(1, tiers.length));
  slider.disabled = tiers.length < 2;
}

function renderPrice() {
  const t = tiers[tier] || tiers[0];
  // Самый дорогой месяц среди тарифов — база, от которой считается выгода.
  const base = Math.max.apply(null, tiers.map(function (x) { return x.amount / (x.months || 1); }));
  const full = Math.round(base * t.months);
  const saveAmt = full - t.amount;
  const savePct = full > 0 ? Math.round((saveAmt / full) * 100) : 0;

  slider.value = String(tier + 1);
  slider.style.setProperty('--fill', (tiers.length > 1 ? (tier / (tiers.length - 1)) * 100 : 0) + '%');
  ticks.forEach(function (b, i) { b.classList.toggle('is-on', i === tier); });

  byId('price-total').textContent = money(t.amount);
  byId('price-for').textContent = t.full;
  byId('price-permonth').textContent = t.months > 1.2
    ? '≈ ' + money(t.amount / t.months) + ' в месяц'
    : 'все функции включены';

  const save = byId('price-save');
  const show = saveAmt > 0 && savePct >= 3;
  save.hidden = !show;
  if (show) save.textContent = 'Выгода ' + savePct + '% · −' + money(saveAmt);
}

async function loadTiers() {
  try {
    const plans = await billingPlans();
    const list = (plans || []).filter(function (p) { return p.is_public !== false; });
    const recurring = list.filter(function (p) { return p.kind === 'recurring'; });
    const next = (recurring.length ? recurring : list).map(planToTier)
      .sort(function (a, b) { return a.months - b.months; });
    if (!next.length) return;
    tiers = next;
    tier = 0;
    renderTicks();
    renderPrice();
  } catch (e) {}
}

slider.addEventListener('input', function (e) {
  tier = Math.max(0, Math.min(tiers.length - 1, Number(e.target.value) - 1));
  renderPrice();
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

renderTicks();
renderPrice();
renderDownloads();
loadTiers();

// Манифест зеркала: версия и размеры. Не дошёл — кнопки всё равно рабочие.
fetch('/download/latest.json', { cache: 'no-store' })
  .then(function (r) { return r.ok ? r.json() : null; })
  .then(function (rel) {
    if (rel && Array.isArray(rel.files)) { release = rel; renderDownloads(); }
  })
  .catch(function () {});
