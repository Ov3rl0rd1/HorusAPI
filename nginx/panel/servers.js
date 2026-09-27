// Раздел «Ноды»: состояние парка, профили xray, эвакуация, добавление.

import * as api from './admin-api.js';
import { $, fill, renderList, onAction, onForm, notify, ask, ago, intOrNull, orNull } from './bind.js';

const view = $('[data-view="servers"]');

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
}

onAction('refresh-servers', load);

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
