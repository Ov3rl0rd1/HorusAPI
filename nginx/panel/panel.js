// Точка входа панели: разделы, переходы между ними, потеря сессии.
//
// Сюда попадает только админ — nginx отдаёт /panel/* лишь после того, как API
// подтвердит сессию из cookie (см. locations.conf и js/session.js). Проверка
// здесь не повторяется: любой вызов /admin/* всё равно проверяется сервером.

import * as api from './admin-api.js';
import { $, $$, fill, notify, report, whenAuthLost } from './bind.js';
import * as servers from './servers.js';
import * as users from './users.js';
import { loadPayments, loadPromos } from './billing.js';

const views = {
  servers: servers.load,
  users: users.load,
  payments: loadPayments,
  promos: loadPromos
};

async function show(name, params) {
  if (!views[name]) name = 'servers';

  for (const el of $$('[data-view]')) el.hidden = el.dataset.view !== name;
  for (const el of $$('[data-show]')) {
    if (el.dataset.show === name) el.setAttribute('aria-current', 'page');
    else el.removeAttribute('aria-current');
  }
  if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);

  const view = $(`[data-view="${name}"]`);
  view.setAttribute('aria-busy', 'true');
  try {
    notify(view, '', '');
    await views[name](params);
  } catch (err) {
    report(view, err);
  } finally {
    view.removeAttribute('aria-busy');
  }
}

document.addEventListener('click', function (e) {
  const tab = e.target.closest('[data-show]');
  if (!tab) return;
  e.preventDefault();
  show(tab.dataset.show);
});

document.addEventListener('panel:go', (e) => show(e.detail.view, e.detail.params));

// Сессию отозвали или она истекла, пока панель была открыта.
whenAuthLost(function () {
  const lost = $('[data-auth-lost]');
  if (lost) lost.hidden = false;
});

api.whoAmI()
  .then((me) => fill(document.body, { me: me.username }))
  .catch(() => {});

show(location.hash.slice(1));
