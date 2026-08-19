// Инструкции по подключению. Контент лежит в guides/instructions.html и
// правится без участия кода: один <article data-app data-platform> —
// одно приложение. Здесь только чтение файла и отрисовка.
//
// Пути абсолютные: страница живёт по адресу без расширения (/connect), и
// относительный 'guides/…' сломался бы, стоит ей переехать в подпапку.

import { byId, detectOS } from './util.js';

const SOURCE = '/guides/instructions.html';
const BASE = '/guides/';
const LINK_TOKEN = '{{ссылка}}';

// Какую платформу открыть первой на этом устройстве.
const OS_PLATFORM = { ios: 'ios', android: 'android', windows: 'windows', mac: 'macos' };

const tabsHost = byId('guide-platforms');
const appsHost = byId('guide-apps');
const panel = byId('guide-panel');
const nameEl = byId('guide-name');
const linkEl = byId('guide-download');
const bodyEl = byId('guide-body');
const statusEl = byId('guide-status');

let groups = [];          // [{ platform, apps: [article] }]
let platform = '';
let app = null;
let subscriptionLink = '';

// ── Пути внутри файла инструкций считаются от папки guides/ ───────────────
function resolveAsset(path) {
  if (!path) return '';
  if (/^(https?:|data:|\/)/i.test(path)) return path;
  return BASE + path.replace(/^\.\//, '');
}

function status(text) {
  statusEl.textContent = text || '';
  statusEl.hidden = !text;
}

// ── Чтение файла ──────────────────────────────────────────────────────────
async function load() {
  status('Загружаем инструкции…');
  let html;
  try {
    const response = await fetch(SOURCE, { cache: 'no-cache' });
    if (!response.ok) throw new Error(String(response.status));
    html = await response.text();
  } catch (e) {
    status('Инструкции не загрузились. Обновите страницу или напишите в поддержку.');
    return;
  }

  const doc = new DOMParser().parseFromString(html, 'text/html');
  const articles = Array.from(doc.querySelectorAll('article[data-app]'));
  if (!articles.length) {
    status('В файле инструкций пока нет ни одного приложения.');
    return;
  }

  // Группируем по платформе, порядок — как в файле.
  const index = {};
  articles.forEach(function (article) {
    const key = (article.getAttribute('data-platform') || 'Другое').trim();
    const id = key.toLowerCase();
    if (!index[id]) {
      index[id] = { id: id, platform: key, apps: [] };
      groups.push(index[id]);
    }
    index[id].apps.push(article);
  });

  status('');
  renderTabs();

  const want = OS_PLATFORM[detectOS()];
  const start = groups.filter(function (g) { return g.id === want; })[0] || groups[0];
  selectPlatform(start.id);
}

// ── Платформы ─────────────────────────────────────────────────────────────
function renderTabs() {
  tabsHost.textContent = '';
  tabsHost.hidden = groups.length < 2;
  groups.forEach(function (group) {
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.className = 'guide-tab';
    tab.textContent = group.platform;
    tab.setAttribute('data-platform', group.id);
    tab.addEventListener('click', function () { selectPlatform(group.id); });
    tabsHost.appendChild(tab);
  });
}

function selectPlatform(id) {
  platform = id;
  Array.from(tabsHost.children).forEach(function (tab) {
    tab.classList.toggle('is-on', tab.getAttribute('data-platform') === id);
  });
  const group = groups.filter(function (g) { return g.id === id; })[0];
  renderApps(group.apps);
  selectApp(group.apps[0]);
}

// ── Приложения ────────────────────────────────────────────────────────────
function renderApps(apps) {
  appsHost.textContent = '';
  apps.forEach(function (article) {
    const name = article.getAttribute('data-app') || 'Приложение';
    const note = article.getAttribute('data-note') || '';
    const icon = resolveAsset(article.getAttribute('data-icon'));

    const card = document.createElement('button');
    card.type = 'button';
    card.className = 'guide-app';
    card.setAttribute('aria-pressed', 'false');

    const box = document.createElement('span');
    box.className = 'guide-app__icon';
    if (icon) {
      const img = document.createElement('img');
      img.src = icon;
      img.alt = '';
      // Иконки нет — оставляем букву названия.
      img.addEventListener('error', function () {
        box.textContent = '';
        box.appendChild(letter(name));
      });
      box.appendChild(img);
    } else {
      box.appendChild(letter(name));
    }
    card.appendChild(box);

    const text = document.createElement('span');
    text.className = 'guide-app__text';
    const title = document.createElement('span');
    title.className = 'guide-app__name';
    title.textContent = name;
    text.appendChild(title);
    if (note) {
      const sub = document.createElement('span');
      sub.className = 'guide-app__note';
      sub.textContent = note;
      text.appendChild(sub);
    }
    card.appendChild(text);

    const mark = document.createElement('span');
    mark.className = 'guide-app__mark';
    mark.textContent = '✓';
    card.appendChild(mark);

    card.addEventListener('click', function () { selectApp(article); });
    appsHost.appendChild(card);
    article.__card = card;
  });
}

function letter(name) {
  const span = document.createElement('span');
  span.className = 'guide-app__letter';
  span.textContent = name.trim().charAt(0).toUpperCase();
  return span;
}

function selectApp(article) {
  app = article;
  Array.from(appsHost.children).forEach(function (card) {
    const on = card === article.__card;
    card.classList.toggle('is-on', on);
    card.setAttribute('aria-pressed', on ? 'true' : 'false');
  });
  renderPanel(article);
}

// ── Инструкция ────────────────────────────────────────────────────────────
function renderPanel(article) {
  panel.hidden = false;
  nameEl.textContent = article.getAttribute('data-app') || '';

  const href = article.getAttribute('data-link');
  linkEl.hidden = !href;
  if (href) {
    linkEl.href = href;
    linkEl.textContent = article.getAttribute('data-link-label') || 'Скачать';
  }

  const body = document.importNode(article, true);
  fixImages(body);
  fillLinkSlots(body);

  bodyEl.textContent = '';
  while (body.firstChild) bodyEl.appendChild(body.firstChild);
}

function fixImages(root) {
  Array.from(root.querySelectorAll('img')).forEach(function (img) {
    // Заглушку ставим и по событию, и синхронной проверкой: битая картинка
    // может успеть провалиться до навешивания слушателя.
    function missing() {
      if (!img.isConnected && !img.parentNode) return;
      const box = document.createElement('div');
      box.className = 'guide-img-missing';
      box.textContent = img.alt || 'Скриншот ещё не добавлен';
      const figure = img.closest('figure');
      (figure || img).replaceWith(box);
    }
    function check() { if (img.complete && img.naturalWidth === 0) missing(); }

    img.addEventListener('error', missing);
    img.addEventListener('load', check);
    // lazy не годится: у битой картинки нулевой бокс, загрузка не стартует
    // и error не наступает никогда.
    img.removeAttribute('loading');
    img.src = resolveAsset(img.getAttribute('src'));
    check();
    img.addEventListener('click', function () { zoom(img.src, img.alt); });
  });
}

// {{ссылка}} в тексте и <div data-subscription-link> превращаются в блок
// с личной ссылкой подписки.
function fillLinkSlots(root) {
  Array.from(root.querySelectorAll('[data-subscription-link]')).forEach(function (slot) {
    slot.replaceWith(linkBlock());
  });

  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const hits = [];
  let node;
  while ((node = walker.nextNode())) {
    if (node.nodeValue.indexOf(LINK_TOKEN) >= 0) hits.push(node);
  }
  hits.forEach(function (text) {
    const parts = text.nodeValue.split(LINK_TOKEN);
    const frag = document.createDocumentFragment();
    parts.forEach(function (part, i) {
      if (part) frag.appendChild(document.createTextNode(part));
      if (i < parts.length - 1) frag.appendChild(linkBlock());
    });
    text.replaceWith(frag);
  });
}

function linkBlock() {
  const box = document.createElement('div');
  box.className = 'guide-link';

  const value = document.createElement('pre');
  value.className = 'guide-link__value' + (subscriptionLink ? '' : ' guide-link__value--empty');
  value.textContent = subscriptionLink || 'Нажмите «Подключить» выше — ссылка появится здесь.';
  box.appendChild(value);

  if (subscriptionLink) {
    const copy = document.createElement('button');
    copy.type = 'button';
    copy.className = 'chip';
    copy.setAttribute('data-label', 'Скопировать ссылку');
    copy.textContent = 'Скопировать ссылку';
    copy.addEventListener('click', function () { copyLink(copy, value); });
    box.appendChild(copy);
  }
  return box;
}

async function copyLink(button, valueEl) {
  const label = button.getAttribute('data-label');
  try {
    await navigator.clipboard.writeText(subscriptionLink);
    button.textContent = 'Скопировано';
  } catch (e) {
    // Clipboard API недоступен — выделяем текст, человек копирует сам.
    const range = document.createRange();
    range.selectNodeContents(valueEl);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    button.textContent = 'Выделено — Ctrl+C';
  }
  button.classList.add('is-done');
  setTimeout(function () {
    button.textContent = label;
    button.classList.remove('is-done');
  }, 1800);
}

// ── Просмотр картинки ─────────────────────────────────────────────────────
function zoom(src, alt) {
  const layer = document.createElement('div');
  layer.className = 'guide-zoom';
  layer.setAttribute('role', 'dialog');

  const img = document.createElement('img');
  img.src = src;
  img.alt = alt || '';
  layer.appendChild(img);

  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'guide-zoom__close';
  close.setAttribute('aria-label', 'Закрыть');
  close.textContent = '×';
  layer.appendChild(close);

  function hide() {
    layer.remove();
    window.removeEventListener('keydown', onKey);
  }
  function onKey(e) { if (e.key === 'Escape') hide(); }

  layer.addEventListener('click', hide);
  window.addEventListener('keydown', onKey);
  document.body.appendChild(layer);
}

// ── Ссылка подписки приходит из connect.js ────────────────────────────────
// Инструкции написаны под клиенты с Hysteria2, поэтому connect.js шлёт сюда
// именно её, а не VLESS.
window.addEventListener('horus:link', function (e) {
  subscriptionLink = e.detail || '';
  if (app) renderPanel(app);
});

load();
