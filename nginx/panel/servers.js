// Раздел «Ноды»: состояние парка, профили xray, эвакуация, добавление,
// карточка одной ноды с её пользователями и перенос пользователя с ноды.

import * as api from './admin-api.js';
import {
  $, fill, attach, renderList, onAction, onForm, notify, ask, go, options,
  ago, date, dateTime, intOrNull, orNull
} from './bind.js';

const view = $('[data-view="servers"]');
const card = $('[data-card="server"]', view);

// Последний прочитанный список нод — из него строится выбор «куда переносить».
let nodes = [];
// Открытая карточка: ServerDetail.
let opened = null;

// Куда можно перенести пользователя с ноды `fromId`: активные, не она сама.
// Заполненные показываются, но выбрать их нельзя — API всё равно откажет.
export function moveTargets(list, fromId) {
  return list
    .filter((s) => s.is_active && s.id !== fromId)
    .map((s) => ({
      value: s.id,
      label: `${s.name} — ${s.country} · ${s.reserved_count}/${s.max_reservations}` +
             (s.reserved_count >= s.max_reservations ? ' (заполнена)' : ''),
      disabled: s.reserved_count >= s.max_reservations
    }));
}

// Текст отчёта о переносе одного пользователя.
export function moveReport(r) {
  const lines = [`${r.username} перенесён на ${r.server_name} (#${r.server_id}).`];
  if (r.problems && r.problems.length) {
    lines.push('Нода назначения не подтвердила пользователя — он появится на ней после её сверки с API:');
    lines.push(...r.problems);
  }
  return lines.join('\n');
}

export async function load() {
  const [list, profiles, fleet] = await Promise.all([
    api.servers(), api.serverProfiles(), api.fleetProfile()
  ]);
  const profileOf = new Map(profiles.map((p) => [p.id, p]));

  fill(view, {
    fleetProfile: fleet.default_profile || 'не задан',
    fleetNodes: fleet.nodes_total,
    fleetOverridden: fleet.nodes_overridden
  });

  nodes = list;

  renderList('servers', list, function (row, s) {
    const p = profileOf.get(s.id) || {};
    row.dataset.key = s.id;
    fill(row, {
      id: s.id,
      name: s.name,
      place: s.country + ' · ' + s.city,
      host: s.host,
      seats: s.reserved_count + ' / ' + s.max_reservations,
      online: s.current_load + ' / ' + s.max_clients,
      profile: s.profile || '—',
      assigned: p.assigned_profile || '—',
      renderError: p.render_error || '',
      warnings: (p.warnings || []).join('; '),
      version: s.agent_version || '—',
      seen: ago(s.last_registered_at),
      ping: ''
    }, {
      active: s.is_active,
      full: s.reserved_count >= s.max_reservations,
      inSync: p.in_sync !== false,
      override: !!p.desired_profile,
      renderError: !!p.render_error,
      warnings: !!(p.warnings && p.warnings.length),
      pinged: false
    });
  });

  // Открытая карточка обновляется вместе со списком — после эвакуации или
  // переноса в ней остались бы прежние пользователи.
  if (opened) await openServer(opened.node.id);
}

onAction('refresh-servers', load);

// ── Карточка ноды ────────────────────────────────────────────────────────

function access(u) {
  if (u.is_admin) return 'админ';
  if (!u.expires_at) return 'нет подписки';
  const end = new Date(u.expires_at);
  return (end > new Date() ? 'до ' : 'истекла ') + date(end);
}

async function openServer(id) {
  let detail;
  try {
    detail = await api.server(id);
  } catch (err) {
    // Ноду удалили, пока карточка была открыта.
    if (err && err.status === 404) { closeServer(); return; }
    throw err;
  }

  const n = detail.node;
  opened = detail;
  card.hidden = false;
  attach(card, n);

  let pretty = n.offers_json;
  try { pretty = JSON.stringify(JSON.parse(n.offers_json), null, 2); } catch (e) { /* как есть */ }

  fill(card, {
    id: n.id,
    name: n.name,
    place: n.country + ' · ' + n.city,
    host: n.host,
    masquerade: n.masquerade_url || `https://${n.host} (по умолчанию)`,
    seats: n.reserved_count + ' / ' + n.max_reservations,
    reserved: n.reserved_count,
    bound: n.bound_users,
    holds: n.pending_holds,
    soft: n.max_clients,
    online: n.current_load,
    version: n.agent_version || '—',
    seen: n.last_registered_at ? dateTime(n.last_registered_at) + ' (' + ago(n.last_registered_at) + ')' : 'никогда',
    profile: n.profile || '—',
    assigned: n.assigned_profile || 'на усмотрение ноды',
    profileHash: n.profile_hash || '—',
    configHash: n.config_hash || '—',
    renderError: n.render_error || '',
    warnings: (n.warnings || []).join('; '),
    offerCount: detail.offers.length,
    offersJson: pretty
  }, {
    active: n.is_active,
    full: n.reserved_count >= n.max_reservations,
    drift: n.reserved_count !== n.bound_users + n.pending_holds,
    override: !!n.desired_profile,
    inSync: n.in_sync,
    renderError: !!n.render_error,
    warnings: !!(n.warnings && n.warnings.length)
  });

  renderList('offers', detail.offers, function (row, o) {
    fill(row, {
      id: o.id || '—',
      label: o.label || '',
      tag: o.tag || '',
      protocol: o.protocol || '—',
      audience: o.audience && o.audience.length ? o.audience.join(', ') : 'все',
      uri: o.has_uri ? 'есть' : 'нет'
    });
  });

  renderList('node-users', detail.users, function (row, u) {
    row.dataset.key = u.id;
    fill(row, {
      id: u.id,
      username: u.username,
      email: u.email || '—',
      access: access(u),
      lastSeen: u.last_disconnect_at
        ? ago(u.last_disconnect_at) + (u.last_disconnect_reason ? ' · ' + u.last_disconnect_reason : '')
        : '—'
    }, { admin: u.is_admin });
  });

  options(card, 'move-targets', moveTargets(nodes, n.id));
}

function closeServer() {
  opened = null;
  card.hidden = true;
}

onAction('open-server', ({ item }) => openServer(item.id));
onAction('refresh-server', async () => { if (opened) { nodes = await api.servers(); await openServer(opened.node.id); } });
onAction('close-server', closeServer);
onAction('node-user-open', ({ item }) => go('users', { q: item.username, pick: item.username }));

// Перенос одного пользователя. Нода остаётся в ротации — в отличие от эвакуации.
onAction('move-node-user', async function ({ item }) {
  const n = opened.node;
  const select = $('select[data-options="move-targets"]', card);
  const to = intOrNull(select.value);
  const where = to == null ? 'наименее загруженную другую ноду' : select.selectedOptions[0].textContent;

  if (!(await ask.confirm(`Перенести ${item.username} с ${n.name} на ${where}?`))) return;

  const r = await api.moveUser(n.id, item.id, to);
  await load();
  notify(view, r.problems && r.problems.length ? 'warn' : 'ok', moveReport(r));
});

onAction('ping-servers', async function () {
  const results = await api.pingServers();
  for (const r of results) {
    const row = $(`[data-list="servers"] [data-key="${r.id}"]`);
    if (!row) continue;
    const text = r.reachable ? 'HTTP ' + r.statusCode : (r.error || 'не отвечает');
    fill(row, { ping: text }, { pinged: true, reachable: r.reachable });
  }
});

// Эвакуация — сначала узел выводится из ротации, потом с него уводят всех.
// Частичную доводит повторный запуск, поэтому отчёт показывается целиком.
onAction('evacuate', async function ({ item }) {
  const r = await api.evacuate(item.id);
  const lines = [
    `${item.name}: из ${r.total} перенесено ${r.moved}, осталось ${r.stayed}, с ошибками ${r.failed}.`
  ];
  if (r.stayed) lines.push('Оставшимся не нашлось места — добавьте ёмкость и запустите ещё раз.');
  if (r.problems && r.problems.length) lines.push(...r.problems);
  await load();
  notify(view, r.stayed || r.failed ? 'warn' : 'ok', lines.join('\n'));
});

onAction('activate', async function ({ item }) {
  await api.activate(item.id);
  await load();
  notify(view, 'ok', `${item.name} снова принимает пользователей.`);
});

onAction('remove-server', async function ({ item }) {
  await api.removeServer(item.id);
  await load();
  notify(view, 'ok', `${item.name} удалён.`);
});

onAction('server-profile', async function ({ item }) {
  const current = await api.serverProfiles().then((all) => all.find((p) => p.id === item.id));
  const value = await ask.text(
    `Профиль xray для ${item.name}. Пусто — как у всего парка.`,
    (current && current.desired_profile) || ''
  );
  if (value === null) return;
  await api.setServerProfile(item.id, value.trim());
  await load();
});

onAction('fleet-profile', async function () {
  const fleet = await api.fleetProfile();
  const value = await ask.text(
    'Профиль xray по умолчанию для всего парка. Переключатся все ноды без своего профиля.',
    fleet.default_profile || ''
  );
  if (value === null) return;
  await api.setFleetProfile(value.trim());
  await load();
});

// Пароль ноды нужен при её настройке (NODE_API_PASSWORD). В таблице его нет
// намеренно — только по запросу, в поле, откуда его удобно скопировать.
onAction('server-secret', async function ({ item }) {
  await ask.text(`Пароль ноды ${item.name} (X-API-PASSWORD):`, item.auth_password || '');
});

onForm('add-server', async function (v, form) {
  const res = await api.addServer({
    name: v.name,
    country: v.country,
    city: v.city,
    host: v.host,
    max_clients: intOrNull(v.max_clients) || 0,
    max_reservations: intOrNull(v.max_reservations),
    masquerade_url: orNull(v.masquerade_url),
    auth_password: orNull(v.auth_password)
  });
  form.reset();
  await load();
  notify(view, 'ok', `Нода добавлена, id ${res.id}. Она появится в работе после первой регистрации агента.`);
});
