// Раздел «Пользователи»: поиск, служебная подписка, перенос с ноды, закрытые тарифы.

import * as api from './admin-api.js';
import {
  $, fill, attach, renderList, onAction, onForm, notify, ask, go, options,
  date, dateTime, endOfDay, intOrNull, grantUntil, grantTerm, period
} from './bind.js';
import { moveTargets, moveReport } from './servers.js';

const view  = $('[data-view="users"]');
const card  = $('[data-card="user"]', view);
const query = $('[data-form="user-search"] [name="q"]', view);

let selected = null;

// params.pick — открыть карточку этого пользователя, если он нашёлся
// (переход из ноды или тарифа).
export async function load(params) {
  if (params && typeof params.q === 'string') query.value = params.q;
  const list = await search(query.value.trim());
  if (params && params.pick) {
    const hit = list.find((u) => u.username === params.pick);
    if (hit) await select(hit);
  }
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
  if (fresh) await select(fresh);
  return list;
}

async function select(u) {
  selected = u;
  card.hidden = !u;
  if (!u) return;
  attach(card, u);
  fill(card, values(u), flags(u));

  // Ноды — для выбора «куда перенести», тарифы — для «открыть закрытый».
  // Грузятся вместе с карточкой, чтобы список не устаревал между открытиями.
  const [servers, plans, traffic] = await Promise.all([
    api.servers(), api.plans(), api.userTraffic(u.username).catch(() => []), loadGrants()
  ]);
  if (selected !== u) return;   // пока грузилось, открыли другого

  fill(card, { traffic: trafficText(traffic) });

  options(card, 'move-targets', moveTargets(servers, u.current_server_id));

  const closed = plans.filter((p) => !p.is_public && p.is_active);
  options(card, 'closed-plans', closed.map((p) => ({
    value: p.code,
    label: `${p.title} (${p.code}) — ${p.amount} ₽ / ${period(p.interval_unit, p.interval_count)}`
  })));
  fill(card, {}, { closedPlans: closed.length > 0 });
}

// Трафик за текущий месяц (UTC) — то, против чего считается месячный лимит. Хранится
// в API по пользователю, поэтому не обнуляется при смене сервера.
const gb = (bytes) => (bytes / 1e9).toLocaleString('ru-RU', { maximumFractionDigits: 1 }) + ' ГБ';

function trafficText(months) {
  const now = new Date().toISOString().slice(0, 7);
  const m = (months || []).find((x) => String(x.month).slice(0, 7) === now);
  if (!m) return 'нет данных';
  return gb(m.total_bytes) + (m.olcrtc_bytes ? ', из них olcRTC ' + gb(m.olcrtc_bytes) : '') +
    (m.server_name ? ' · последний отчёт: ' + m.server_name : '');
}

async function loadGrants() {
  const u = selected;
  const grants = await api.userGrants(u.username);
  if (selected !== u) return;

  renderList('user-grants', grants, function (row, g) {
    row.dataset.key = g.plan_id;
    fill(row, {
      plan: g.plan_title,
      code: g.plan_code,
      term: grantTerm(g.expires_at),
      by: g.granted_by || '—',
      created: date(g.created_at)
    }, {
      public: g.plan_is_public,
      expired: !!g.expires_at && new Date(g.expires_at) <= new Date()
    });
  });
}

// После действия перечитывается и список, и сам пользователь: в текущую выдачу
// он может не попадать, а карточка должна показать новое состояние.
// search() уже обновляет карточку, если пользователь есть в выдаче, — второй раз
// её не перечитываем: у админки лимит запросов в минуту.
async function refresh() {
  const id = selected.id;
  const list = await search(query.value.trim());
  if (list.some((u) => u.id === id)) return;
  const fresh = (await api.users(String(id))).find((u) => u.id === id);
  if (fresh) await select(fresh);
}

onForm('user-search', (v) => search(v.q || ''));
onAction('pick-user', ({ item }) => select(item));
onAction('close-user', () => select(null));
onAction('user-payments', () => go('payments', { user: selected.username }));
onAction('user-referral', () => go('referrals', { username: selected.username }));

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

// Перенос с текущей ноды. Нода остаётся в ротации — это не эвакуация.
onForm('move-user', async function (v, form) {
  const u = selected;
  const to = intOrNull(v.to);
  const select = $('select[name="to"]', form);
  const where = to == null ? 'наименее загруженную другую ноду' : select.selectedOptions[0].textContent;

  if (!(await ask.confirm(`Перенести ${u.username} с ${u.server_name} на ${where}?`))) return;

  const r = await api.moveUser(u.current_server_id, u.id, to);
  await refresh();
  notify(view, r.problems && r.problems.length ? 'warn' : 'ok', moveReport(r));
});

onForm('grant-plan', async function (v, form) {
  const until = grantUntil(v);
  await api.grantPlan(selected.username, v.plan_code, until);
  form.reset();
  await loadGrants();
  notify(view, 'ok', `${selected.username} может купить тариф ${v.plan_code} ${until ? 'до ' + date(until) : 'бессрочно'}.`);
});

onAction('revoke-user-grant', async function ({ item }) {
  await api.revokeGrant(item.username, item.plan_code);
  await loadGrants();
  notify(view, 'ok', `${item.username}: тариф ${item.plan_code} закрыт.`);
});
