// Страница входа в мониторинг. Два пути:
//
//  * открыта из Telegram — в адресе есть #tgWebAppData=…: отправляем эти данные
//    gate, он проверяет подпись ботом и что это владелец. Руками ничего вводить
//    не нужно, и при каждом открытии мини-приложения вход обновляется сам;
//  * открыта в обычном браузере — форма с ключом доступа.
//
// telegram-web-app.js сознательно не подключается. Всё, что от него нужно, — это
// initData из адреса и два события (ready, expand); без него на странице входа
// нет чужого кода, а открытая в обычном браузере она не ждёт telegram.org.

(function () {
  'use strict';

  var DEFAULT_NEXT = '/vmui/#/dashboards';
  var body = document.body;

  function $$(sel) { return Array.prototype.slice.call(document.querySelectorAll(sel)); }

  function show() {
    var wanted = Array.prototype.slice.call(arguments);
    $$('[data-state]').forEach(function (el) { el.hidden = wanted.indexOf(el.dataset.state) < 0; });
  }

  function slot(name, value) {
    $$('[data-slot="' + name + '"]').forEach(function (el) { el.textContent = value; });
  }

  // Та же проверка, что в gate: только свой путь, иначе страница входа
  // становится инструментом фишинга.
  function safeNext(v) {
    if (!v || v.length > 2048 || v.charAt(0) !== '/' || v.charAt(1) === '/' || v.charAt(1) === '\\') return DEFAULT_NEXT;
    if (/[\u0000-\u001f\\]/.test(v) || v === '/auth' || v.indexOf('/auth/') === 0) return DEFAULT_NEXT;
    return v;
  }

  var query = new URLSearchParams(location.search);
  var launch = new URLSearchParams(location.hash.slice(1));
  var initData = launch.get('tgWebAppData');

  // Фрагмент, с которым открывали vmui (#/cardinality и т.п.), переживает
  // редирект на эту страницу — вернём на него же. Кроме данных запуска Telegram.
  var next = safeNext(query.get('next'));
  if (!initData && location.hash.length > 1 && next.indexOf('#') < 0) next += location.hash;

  // Telegram Web показывает мини-приложение во фрейме — cookie там нужна особая.
  var embedded = (function () {
    try { return window.self !== window.top; } catch (e) { return true; }
  })();

  // То же, что делает официальный скрипт: мобильные клиенты и Desktop слушают
  // TelegramWebviewProxy, веб-версия — postMessage из фрейма.
  function tgEvent(type, data) {
    try {
      if (window.TelegramWebviewProxy && window.TelegramWebviewProxy.postEvent) {
        window.TelegramWebviewProxy.postEvent(type, JSON.stringify(data || {}));
      } else if (embedded) {
        window.parent.postMessage(JSON.stringify({ eventType: type, eventData: data || {} }), '*');
      }
    } catch (e) { /* только оформление — вход от этого не зависит */ }
  }

  // Цвета темы Telegram, чтобы страница не мигала белым в тёмном клиенте.
  function applyTheme(raw) {
    var map = { bg_color: '--bg', text_color: '--fg', hint_color: '--muted', button_color: '--accent',
                button_text_color: '--accent-fg', secondary_bg_color: '--soft' };
    try {
      var theme = JSON.parse(raw || '{}');
      Object.keys(map).forEach(function (k) {
        if (/^#[0-9a-f]{6}$/i.test(theme[k] || '')) document.documentElement.style.setProperty(map[k], theme[k]);
      });
    } catch (e) { /* без темы — значит, со своими цветами */ }
  }

  function minutes(seconds) { return String(Math.max(1, Math.ceil((parseInt(seconds, 10) || 60) / 60))); }

  // Браузер, запретивший сторонние cookie, во фрейме Telegram Web отказывает
  // странице и в localStorage — а vmui обращается к нему без проверки и
  // остаётся белым листом. Такой случай уводим в отдельную вкладку.
  function storageWorks() {
    try {
      window.localStorage.setItem('horus-probe', '1');
      window.localStorage.removeItem('horus-probe');
      return true;
    } catch (e) { return false; }
  }

  function offerHandoff(url) {
    $$('[data-link="handoff"]').forEach(function (a) { a.href = url; });
    show('frame-blocked');
  }

  // Браузер, наглухо запретивший cookie, «примет» вход и не пришлёт cookie ни с
  // одним следующим запросом — получится бесконечный круг между этой страницей и
  // vmui. Поэтому сначала спрашиваем, дошла ли она.
  function proceed(handoff) {
    if (embedded && handoff && !storageWorks()) { offerHandoff(handoff); return Promise.resolve(); }
    return fetch('/auth/whoami', { credentials: 'same-origin', cache: 'no-store' }).then(function (r) {
      if (r.ok) location.replace(next);
      else if (handoff) offerHandoff(handoff);
      else show('no-cookie');
    });
  }

  function telegram() {
    if (body.dataset.tg !== '1') { show('tg-off'); return; }
    show('telegram');
    applyTheme(launch.get('tgWebAppThemeParams'));
    tgEvent('web_app_ready');
    tgEvent('web_app_expand');

    fetch('/auth/telegram', {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({ init_data: initData, embedded: embedded ? '1' : '0' }).toString()
    })
      .then(function (r) {
        return r.json().catch(function () { return {}; }).then(function (b) { return { ok: r.ok, body: b }; });
      })
      .then(function (res) {
        if (res.ok) return proceed(res.body && res.body.handoff);
        var code = res.body && res.body.error;
        if (code === 'slow') { slot('retry', minutes(res.body.retry)); show('slow'); return; }
        show(code === 'not_allowed' ? 'denied'
           : code === 'expired'     ? 'expired'
           : code === 'disabled'    ? 'tg-off'
           : 'invalid');
      })
      .catch(function () { show('network'); });
  }

  function keyForm(extra) {
    $$('[data-field="next"]').forEach(function (el) { el.value = next; });
    $$('[data-field="embedded"]').forEach(function (el) { el.value = embedded ? '1' : '0'; });

    var error = query.get('error');
    if (error === 'slow') { slot('retry', minutes(query.get('retry'))); show('slow', 'key'); }
    else show.apply(null, (extra || []).concat('key'));
    $$('[data-error]').forEach(function (el) { el.hidden = el.dataset.error !== error; });

    var input = document.getElementById('key');
    if (input) input.focus();
  }

  function browser() {
    var extra = query.get('bye') ? ['bye'] : [];
    if (body.dataset.key !== '1') { show.apply(null, extra.concat('nothing')); return; }
    if (query.get('error') || extra.length) { keyForm(extra); return; }

    // Уже вошли (например, вернулись по закладке) — сразу дальше.
    fetch('/auth/whoami', { credentials: 'same-origin', cache: 'no-store' })
      .then(function (r) { if (r.ok) location.replace(next); else keyForm(); })
      .catch(function () { keyForm(); });
  }

  if (initData) telegram(); else browser();
})();
