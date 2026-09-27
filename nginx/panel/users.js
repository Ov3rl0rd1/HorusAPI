// Раздел «Пользователи»: поиск, служебная подписка, закрытые тарифы.

import * as api from './admin-api.js';
import { $, fill, attach, renderList, onAction, onForm, notify, go, date, dateTime, endOfDay } from './bind.js';

const view  = $('[data-view="users"]');
const card  = $('[data-card="user"]', view);
const query = $('[data-form="user-search"] [name="q"]', view);

let selected = null;

export async function load(params) {
  if (params && typeof params.q === 'string') query.value = params.q;
  await search(query.value.trim());
}

function access(u) {
  if (u.is_admin) return 'админ — доступ без подписки';
  if (!u.expires_at) return 'нет подписки';
  const end = new Date(u.expires_at);
  return (end > new Date() ? 'до ' : 'истекла ') + date(end);
}

function values(u) {
  return {
    id: u.id,
    username: u.username,
    email: u.email || '—',
    created: dateTime(u.created_at),
    access: access(u),
    server: u.server_name ? `${u.server_name} (#${u.current_server_id})` : '—'
  };
}

function flags(u) {
  return {
    verified: u.email_verified,
    admin: u.is_admin,
    enabled: u.is_active,
    hasAccess: u.is_admin || (!!u.expires_at && new Date(u.expires_at) > new Date()),
    bound: u.current_server_id != null
  };
}

async function search(q) {
  const list = await api.users(q);
  renderList('users', list, function (row, u) {
    row.dataset.key = u.id;
    fill(row, values(u), flags(u));
  });

  // Карточка открытого пользователя обновляется вместе со списком — иначе после
  // «выдать доступ» в ней остался бы прежний срок.
  const fresh = selected && list.find((u) => u.id === selected.id);
  if (fresh) select(fresh);
}

function select(u) {
  selected = u;
  card.hidden = !u;
  if (!u) return;
  attach(card, u);
  fill(card, values(u), flags(u));
}

// После действия перечитывается и список, и сам пользователь: в текущую выдачу
// он может не попадать, а карточка должна показать новое состояние.
async function refresh() {
  await search(query.value.trim());
  const fresh = (await api.users(String(selected.id))).find((u) => u.id === selected.id);
  if (fresh) select(fresh);
}

onForm('user-search', (v) => search(v.q || ''));
onAction('pick-user', ({ item }) => select(item));
onAction('close-user', () => select(null));
onAction('user-payments', () => go('payments', { user: selected.username }));

onForm('comp', async function (v) {
  await api.grantComp(selected.username, endOfDay(v.until));
  await refresh();
  notify(view, 'ok', `${selected.username}: доступ до ${date(endOfDay(v.until))}.`);
});

onAction('revoke-comp', async function () {
  await api.revokeComp(selected.username);
  await refresh();
  notify(view, 'ok', `${selected.username}: служебный доступ снят, место на ноде освобождено.`);
});

onForm('grant-plan', async function (v, form) {
  await api.grantPlan(selected.username, v.plan_code, endOfDay(v.until));
  form.reset();
  notify(view, 'ok', `${selected.username} теперь может купить тариф ${v.plan_code}.`);
});
