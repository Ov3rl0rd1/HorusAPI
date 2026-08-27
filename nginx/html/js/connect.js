import { whoAmI, serverCandidates, selectServer, connectInfo, subscriptionUrl } from './endpoints.js';
import { requireSession, clearSession } from './session.js';
import { byId, plural } from './util.js';

const states = {
  loading: byId('state-loading'),
  picker: byId('state-picker'),
  ready: byId('state-ready'),
  blocked: byId('state-blocked')
};
function show(name) {
  Object.keys(states).forEach(function (k) { states[k].classList.toggle('is-active', k === name); });
}

byId('logout').addEventListener('click', function () {
  clearSession();
  location.replace('/login');
});

let me = null;
let bound = null;      // BoundServer
let links = null;      // ConnectResponse
let moving = false;    // сервер уже выбран, идёт смена

function place(server) {
  if (!server) return '';
  return [server.city, server.country].filter(Boolean).join(', ');
}

function label(server) {
  if (!server) return '';
  return server.name || place(server) || server.host || ('Сервер ' + server.id);
}

// ── Ошибки в человеческие фразы ───────────────────────────────────────────
function blocked(title, note) {
  byId('blocked-title').textContent = title;
  byId('blocked-note').textContent = note;
  show('blocked');
}

function handle(err, say) {
  if (err.isAuth) { clearSession(); location.replace('/login'); return true; }
  if (err.status === 403) {
    blocked('Подписка неактивна', err.message || 'Подключение доступно, пока действует подписка.');
    return true;
  }
  if (say) {
    say(err.isNetwork
      ? 'Сеть недоступна. Проверьте соединение и попробуйте ещё раз.'
      : (err.message || 'Сервер ответил ошибкой. Попробуйте позже.'));
  }
  return false;
}

function pickerMsg(text, kind) {
  const box = byId('picker-msg');
  box.textContent = text || '';
  box.className = 'msg msg--' + (kind || 'error') + (text ? ' is-shown' : '');
}

// ── Выбор сервера ─────────────────────────────────────────────────────────
function loadPercent(s) {
  const max = Number(s.max_clients) || 0;
  if (!max) return null;
  const used = (Number(s.current_load) || 0) + (Number(s.reserved_count) || 0);
  return Math.max(0, Math.min(100, Math.round((used / max) * 100)));
}

function freeSlots(s) {
  const max = Number(s.max_clients) || 0;
  if (!max) return null;
  return Math.max(0, max - (Number(s.current_load) || 0) - (Number(s.reserved_count) || 0));
}

function renderCandidates(list) {
  const host = byId('picker-list');
  host.textContent = '';
  if (!list.length) {
    pickerMsg('Свободных серверов сейчас нет. Попробуйте через несколько минут.');
    return;
  }

  list.forEach(function (s) {
    const row = document.createElement('button');
    row.type = 'button';
    row.className = 'srv srv--pick';

    const place_ = document.createElement('div');
    place_.className = 'srv__place';
    const name = document.createElement('div');
    name.className = 'srv__name';
    name.textContent = place(s) || s.host;
    const where = document.createElement('div');
    where.className = 'srv__where';
    const free = freeSlots(s);
    where.textContent = free === null
      ? s.host
      : free + ' свободн' + plural(free, ['ое место', 'ых места', 'ых мест']);
    place_.appendChild(name);
    place_.appendChild(where);
    row.appendChild(place_);

    const pct = loadPercent(s);
    if (pct !== null) {
      const load = document.createElement('div');
      load.className = 'srv__load';
      const cap = document.createElement('div');
      cap.className = 'srv__pct';
      cap.textContent = 'Загрузка ' + pct + '%';
      const bar = document.createElement('div');
      bar.className = 'bar';
      const fill = document.createElement('div');
      fill.className = 'bar__fill' + (pct >= 85 ? ' bar__fill--busy' : '');
      fill.style.width = pct + '%';
      bar.appendChild(fill);
      load.appendChild(cap);
      load.appendChild(bar);
      row.appendChild(load);
    }

    if (bound && String(bound.id) === String(s.id)) {
      const here = document.createElement('div');
      here.className = 'srv__here';
      here.textContent = 'ТЕКУЩИЙ';
      row.appendChild(here);
      row.disabled = true;
    }

    row.addEventListener('click', function () { choose(s.id, row); });
    host.appendChild(row);
  });
}

async function openPicker(isMove) {
  moving = !!isMove;
  byId('picker-title').textContent = isMove ? 'Сменить сервер' : 'Выберите сервер';
  byId('picker-warn').hidden = !isMove;
  byId('picker-back').hidden = !isMove;
  pickerMsg('');
  byId('picker-list').textContent = '';
  show('picker');

  try {
    renderCandidates(await serverCandidates());
  } catch (err) {
    handle(err, pickerMsg);
  }
}

async function choose(serverId, row) {
  const auto = byId('picker-auto');
  pickerMsg('');
  if (row) { row.disabled = true; row.classList.add('is-busy'); }
  auto.disabled = true;

  try {
    bound = await selectServer(serverId);
    await loadLinks();
  } catch (err) {
    if (err.status === 409) pickerMsg('На этом сервере закончились места. Выберите другой или включите автоподбор.');
    else if (err.status === 404) pickerMsg('Сервер больше недоступен. Обновите список.');
    else handle(err, pickerMsg);
    if (row) { row.disabled = false; row.classList.remove('is-busy'); }
  } finally {
    auto.disabled = false;
  }
}

byId('picker-auto').addEventListener('click', function () { choose(null, null); });
byId('picker-back').addEventListener('click', function () { show('ready'); });
byId('change-server').addEventListener('click', function () { openPicker(true); });
byId('retry').addEventListener('click', start);

// ── Готовое подключение ───────────────────────────────────────────────────
function renderReady() {
  const server = (links && links.server) || bound;
  byId('server-name').textContent = label(server);
  byId('server-note').textContent = [place(server), server && server.host].filter(Boolean).join(' · ');

  const url = subscriptionUrl();
  byId('link').textContent = url || 'Ссылка недоступна — войдите заново.';
  byId('open-link').href = url;
  byId('open-link').hidden = !url;
  byId('copy-link').hidden = !url;

  const extra = byId('extra-links');
  extra.textContent = '';
  const rows = [];
  if (links && links.hysteria2) rows.push(['Hysteria2', links.hysteria2]);
  (links && links.vless ? links.vless : []).forEach(function (v, i) {
    rows.push(['VLESS' + (links.vless.length > 1 ? ' · ' + (i + 1) : ''), v]);
  });
  rows.forEach(function (pair) {
    const item = document.createElement('div');
    item.className = 'proto';
    const kind = document.createElement('div');
    kind.className = 'proto__kind';
    kind.textContent = pair[0];
    const value = document.createElement('pre');
    value.className = 'secret__box secret__box--link proto__value';
    value.textContent = pair[1];
    const copy = document.createElement('button');
    copy.type = 'button';
    copy.className = 'chip';
    copy.setAttribute('data-label', 'Скопировать');
    copy.textContent = 'Скопировать';
    copy.addEventListener('click', function () { copy_(pair[1], copy, value); });
    item.appendChild(kind);
    item.appendChild(value);
    item.appendChild(copy);
    extra.appendChild(item);
  });
  byId('extra-wrap').hidden = !rows.length;

  // Инструкции подставляют эту ссылку в свои блоки (js/guides.js).
  window.dispatchEvent(new CustomEvent('horus:link', { detail: url }));
  show('ready');
}

async function loadLinks() {
  try {
    links = await connectInfo();
    if (links && links.server) bound = links.server;
  } catch (err) {
    // Ссылка подписки не зависит от этого ответа — покажем экран и без него.
    if (err.status === 409 || err.status === 404) links = null;
    else if (handle(err, null)) return;
    else links = null;
  }
  renderReady();
}

// ── Загрузка ──────────────────────────────────────────────────────────────
async function start() {
  show('loading');
  try {
    me = await whoAmI();
  } catch (err) {
    if (handle(err, null)) return;
    blocked('Не удалось загрузить', err.isNetwork
      ? 'Сеть недоступна. Проверьте соединение и попробуйте ещё раз.'
      : (err.message || 'Сервер ответил ошибкой. Попробуйте позже.'));
    byId('retry').hidden = false;
    return;
  }

  const until = me.subscriptionExpiresAt ? new Date(me.subscriptionExpiresAt).getTime() : 0;
  if (!until || until <= Date.now()) {
    blocked(until ? 'Подписка истекла' : 'Подписка не оформлена',
      'Подключение из браузера доступно, пока действует подписка.');
    return;
  }

  if (me.currentServerId) { bound = { id: me.currentServerId }; loadLinks(); }
  else openPicker(false);
}

// ── Копирование ───────────────────────────────────────────────────────────
function flash(button, text) {
  const label_ = button.getAttribute('data-label');
  button.textContent = text;
  button.classList.add('is-done');
  setTimeout(function () {
    button.textContent = label_;
    button.classList.remove('is-done');
  }, 1800);
}

async function copy_(text, button, box) {
  try {
    await navigator.clipboard.writeText(text);
    flash(button, 'Скопировано');
  } catch (e) {
    // Clipboard API недоступен (нет https или отказ) — выделяем текст,
    // человек копирует сам.
    const range = document.createRange();
    range.selectNodeContents(box);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    flash(button, 'Выделено — Ctrl+C');
  }
}

byId('copy-link').addEventListener('click', function (e) {
  copy_(byId('link').textContent, e.currentTarget, byId('link'));
});

byId('download-link').addEventListener('click', function (e) {
  const blob = new Blob([subscriptionUrl()], { type: 'text/plain' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'horus-subscription.txt';
  a.click();
  URL.revokeObjectURL(a.href);
  flash(e.currentTarget, 'Сохранено');
});

if (requireSession('/login')) start();
