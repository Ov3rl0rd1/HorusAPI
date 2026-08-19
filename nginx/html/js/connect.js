import { connectConfig } from './endpoints.js';
import { requireSession, clearSession } from './session.js';
import { byId } from './util.js';

requireSession('/login');

const states = { idle: byId('state-idle'), ready: byId('state-ready'), blocked: byId('state-blocked') };
function show(name) {
  Object.keys(states).forEach(function (k) { states[k].classList.toggle('is-active', k === name); });
}

byId('logout').addEventListener('click', function () {
  clearSession();
  location.replace('/login');
});

let config = null;

// GET /servers/connect отдаёт готовые ссылки под ключами ApiConsts:
// { "HY2": "hysteria2://…", "VLESS": "vless://…" }. Сервер сам выбирает узел,
// поэтому id не передаём. Ключи читаем терпимо — на случай, если появится
// camelCase-вариант.
function links(cfg) {
  if (!cfg) return { hy2: '', vless: '' };
  const pick = function (keys) {
    for (let i = 0; i < keys.length; i++) {
      const v = cfg[keys[i]];
      if (typeof v === 'string' && v.indexOf('://') > 0) return v;
    }
    return '';
  };
  return {
    hy2: pick(['HY2', 'hy2', 'hysteria', 'hysteriaLink']),
    vless: pick(['VLESS', 'vless', 'vlessLink'])
  };
}

function flash(button, text) {
  const label = button.getAttribute('data-label');
  button.textContent = text;
  button.classList.add('is-done');
  setTimeout(function () {
    button.textContent = label;
    button.classList.remove('is-done');
  }, 1800);
}

async function copy(text, button) {
  try {
    await navigator.clipboard.writeText(text);
    flash(button, 'Скопировано');
  } catch (e) {
    // Clipboard API недоступен (нет https или отказ) — выделяем текст,
    // человек копирует сам.
    const box = button.closest('.secret').querySelector('.secret__box');
    const range = document.createRange();
    range.selectNodeContents(box);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    flash(button, 'Выделено — Ctrl+C');
  }
}

// Одна пара «строка + кнопки». Нет ссылки — прячем кнопки.
function renderLink(boxId, openId, copyId, value) {
  const box = byId(boxId);
  const open = byId(openId);
  const copyBtn = byId(copyId);
  box.textContent = value || 'Эта ссылка недоступна для выбранного сервера.';
  open.hidden = !value;
  copyBtn.hidden = !value;
  if (value) open.href = value;
}

function render() {
  const both = links(config);
  renderLink('link', 'open-link', 'copy-link', both.hy2);
  renderLink('link-vless', 'open-vless', 'copy-vless', both.vless);

  // Инструкции подставляют ссылку в свои блоки (js/guides.js). Все описанные
  // там клиенты работают по Hysteria2, поэтому шлём именно её.
  window.dispatchEvent(new CustomEvent('horus:link', { detail: both.hy2 }));

  show('ready');
}

async function fetchConfig(button) {
  const msg = byId('idle-msg');
  msg.className = 'msg msg--error';
  button.disabled = true;
  button.textContent = 'Резервируем место…';
  try {
    config = await connectConfig();
    render();
  } catch (err) {
    if (err.isAuth) { clearSession(); location.replace('/login'); return; }
    // 403 — подписка кончилась, 404 — свободных серверов нет
    if (err.status === 403 || err.status === 400) {
      byId('blocked-title').textContent = 'Подписка неактивна';
      byId('blocked-note').textContent = err.message || 'Подключение доступно, пока действует подписка.';
      show('blocked');
      return;
    }
    if (err.status === 404) {
      byId('blocked-title').textContent = 'Свободных серверов нет';
      byId('blocked-note').textContent = err.message || 'Все серверы заняты. Попробуйте через несколько минут или напишите в поддержку.';
      show('blocked');
      return;
    }
    msg.textContent = err.isNetwork
      ? 'Сеть недоступна. Проверьте соединение и попробуйте ещё раз.'
      : (err.message || 'Не удалось получить конфигурацию. Попробуйте позже.');
    msg.className = 'msg msg--error is-shown';
  } finally {
    button.disabled = false;
    button.textContent = button.getAttribute('data-label');
  }
}

byId('connect').addEventListener('click', function (e) { fetchConfig(e.currentTarget); });
byId('refresh').addEventListener('click', function (e) { fetchConfig(e.currentTarget); });
byId('copy-link').addEventListener('click', function (e) { copy(byId('link').textContent, e.currentTarget); });
byId('copy-vless').addEventListener('click', function (e) { copy(byId('link-vless').textContent, e.currentTarget); });

byId('download-config').addEventListener('click', function (e) {
  const both = links(config);
  const text = [
    '# Horus VPN — ссылки подписки',
    '# Ссылки личные, не публикуйте их.',
    '',
    both.hy2 ? '# Hysteria2 (основной)\n' + both.hy2 : '',
    both.vless ? '\n# VLESS (запасной)\n' + both.vless : ''
  ].filter(Boolean).join('\n');

  const blob = new Blob([text], { type: 'text/plain;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = 'horus-subscription.txt';
  a.click();
  URL.revokeObjectURL(a.href);
  flash(e.currentTarget, 'Скачано');
});
