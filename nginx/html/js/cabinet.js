import { whoAmI, bestServers, logoutOthers } from './endpoints.js';
import { requireSession, clearSession } from './session.js';
import { byId } from './util.js';

if (requireSession('login.html')) start();

const states = { loading: byId('state-loading'), error: byId('state-error'), ready: byId('state-ready') };
function show(name) {
  Object.keys(states).forEach(function (k) { states[k].classList.toggle('is-active', k === name); });
}

byId('logout').addEventListener('click', function () {
  clearSession();
  location.replace('login.html');
});
byId('retry').addEventListener('click', start);

// ── Подписка ──────────────────────────────────────────────────────────────
const DAY = 86400000;

function subscriptionView(expiresAt) {
  if (!expiresAt) {
    return { state: 'Подписка не оформлена', note: 'Оформите подписку, чтобы подключаться к серверам.', cta: 'Оформить подписку' };
  }
  const end = new Date(expiresAt);
  const left = end.getTime() - Date.now();
  const date = end.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' });
  if (left <= 0) {
    return { state: 'Подписка истекла', note: 'Срок закончился ' + date + '. Продлите, чтобы снова подключаться.', cta: 'Продлить подписку' };
  }
  const days = Math.ceil(left / DAY);
  const word = days % 10 === 1 && days % 100 !== 11 ? 'день'
    : [2, 3, 4].indexOf(days % 10) >= 0 && [12, 13, 14].indexOf(days % 100) < 0 ? 'дня' : 'дней';
  return {
    state: 'Активна ещё ' + days + ' ' + word,
    note: 'Действует до ' + date + ' · до 5 устройств одновременно',
    cta: days <= 7 ? 'Продлить сейчас' : 'Продлить подписку'
  };
}

function renderAccount(me) {
  byId('greeting').textContent = me.username ? 'Привет, ' + me.username : 'Аккаунт';

  const sub = subscriptionView(me.subscriptionExpiresAt);
  byId('sub-state').textContent = sub.state;
  byId('sub-note').textContent = sub.note;
  byId('renew').textContent = sub.cta;

  byId('acc-username').textContent = me.username || '—';
  byId('acc-email').textContent = me.email || '';

  const verified = byId('acc-verified');
  verified.textContent = '';
  const badge = document.createElement('span');
  badge.className = 'badge ' + (me.emailVerified ? 'badge--ok' : 'badge--warn');
  badge.textContent = me.emailVerified ? 'E-mail подтверждён' : 'E-mail не подтверждён';
  verified.appendChild(badge);

  byId('acc-ip').textContent = me.ip || '—';
  byId('acc-ipnote').textContent = [
    me.ipVersion ? 'IPv' + String(me.ipVersion).replace(/^IPv/i, '') : '',
    me.observedAt ? 'на ' + new Date(me.observedAt).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }) : ''
  ].filter(Boolean).join(' · ');
}

// ── Серверы ───────────────────────────────────────────────────────────────
function loadPercent(server) {
  const max = Number(server.max_clients) || 0;
  const now = Number(server.current_load) || 0;
  if (!max) return null;
  return Math.max(0, Math.min(100, Math.round((now / max) * 100)));
}

function renderServers(list, currentServerId) {
  const host = byId('servers');
  host.textContent = '';
  if (!list || !list.length) {
    byId('servers-note').textContent = 'Список серверов пока недоступен.';
    return;
  }

  list.forEach(function (s) {
    const row = document.createElement('div');
    row.className = 'srv';

    const place = document.createElement('div');
    place.className = 'srv__place';
    const name = document.createElement('div');
    name.className = 'srv__name';
    name.textContent = s.name || s.host || ('Сервер ' + s.id);
    const where = document.createElement('div');
    where.className = 'srv__where';
    where.textContent = [s.city, s.country].filter(Boolean).join(', ');
    place.appendChild(name);
    place.appendChild(where);
    row.appendChild(place);

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

    if (currentServerId && String(s.id) === String(currentServerId)) {
      const here = document.createElement('div');
      here.className = 'srv__here';
      here.textContent = 'ВЫ ЗДЕСЬ';
      row.appendChild(here);
    }

    host.appendChild(row);
  });

  byId('servers-note').textContent = 'Автовыбор берёт сервер с наименьшей загрузкой.';
}

function renderCurrentServer(me, list) {
  const found = (list || []).filter(function (s) { return String(s.id) === String(me.currentServerId); })[0];
  const value = byId('acc-server');
  const note = byId('acc-servernote');
  if (!me.currentServerId) {
    value.textContent = 'Не подключено';
    note.textContent = 'Запустите приложение или подключитесь из браузера.';
    return;
  }
  value.textContent = found ? (found.name || found.host) : 'Сервер ' + me.currentServerId;
  note.textContent = found ? [found.city, found.country].filter(Boolean).join(', ') : '';
}

// ── Закрыть другие сессии ─────────────────────────────────────────────────
const others = byId('logout-others');
others.addEventListener('click', async function () {
  const msg = byId('sec-msg');
  msg.className = 'msg msg--error';
  others.disabled = true;
  others.textContent = 'Закрываем…';
  try {
    await logoutOthers();
    msg.textContent = 'Готово. На других устройствах нужно войти заново.';
    msg.className = 'msg msg--ok is-shown';
  } catch (err) {
    if (err.isAuth) { clearSession(); location.replace('login.html'); return; }
    msg.textContent = err.isNetwork ? 'Сеть недоступна. Попробуйте ещё раз.' : (err.message || 'Не удалось закрыть сессии.');
    msg.className = 'msg msg--error is-shown';
  } finally {
    others.disabled = false;
    others.textContent = others.getAttribute('data-label');
  }
});

// ── Загрузка ──────────────────────────────────────────────────────────────
async function start() {
  show('loading');
  try {
    const me = await whoAmI();
    // список серверов не критичен: кабинет открываем и без него
    const list = await bestServers().catch(function () { return []; });
    renderAccount(me);
    renderServers(list, me.currentServerId);
    renderCurrentServer(me, list);
    show('ready');
  } catch (err) {
    if (err.isAuth) { clearSession(); location.replace('login.html'); return; }
    byId('error-text').textContent = err.isNetwork
      ? 'Сеть недоступна. Проверьте соединение и попробуйте ещё раз.'
      : (err.message || 'Сервер ответил ошибкой. Попробуйте позже.');
    show('error');
  }
}
