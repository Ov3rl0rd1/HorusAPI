// Раздел «Тарифы»: каталог, создание и правка, кому открыт закрытый тариф.

import * as api from './admin-api.js';
import {
  $, fill, attach, renderList, onAction, onForm, notify, go, setForm,
  date, grantUntil, grantTerm, intOrNull, period
} from './bind.js';

const view = $('[data-view="plans"]');
const card = $('[data-card="plan"]', view);
const form = $('[data-form="plan"]', card);

// Открытая карточка: PlanAdminItem, или { creating: true } для нового тарифа.
let opened = null;

const KIND = { recurring: 'подписка', one_time: 'разовый' };

export async function load() {
  const list = await api.plans();

  renderList('plans', list, function (row, p) {
    row.dataset.key = p.id;
    fill(row, {
      code: p.code,
      title: p.title,
      tier: p.tier !== 'standard' ? p.tier : '',
      kind: KIND[p.kind] || p.kind,
      period: period(p.interval_unit, p.interval_count),
      amount: p.amount + ' ₽',
      subs: p.live_subscriptions,
      grants: p.is_public ? '—' : p.grants
    }, {
      active: p.is_active,
      public: p.is_public
    });
  });

  // Карточка открытого тарифа обновляется вместе со списком.
  if (opened && !opened.creating) {
    const fresh = list.find((p) => p.id === opened.id);
    if (fresh) await open(fresh, false); else close();
  }
  return list;
}

// Тело PUT/POST из формы. Код уходит только при создании.
function body(v, creating) {
  return {
    code: creating ? v.code : undefined,
    title: v.title,
    tier: v.tier || null,
    kind: v.kind,
    interval_unit: v.interval_unit,
    interval_count: intOrNull(v.interval_count),
    amount: intOrNull(v.amount),
    is_public: !!v.is_public,
    is_active: !!v.is_active
  };
}

// fillForm — заполнить поля заново; при обновлении списка после действия этого
// не делается, чтобы не стереть недописанную правку.
async function open(p, fillForm = true) {
  opened = p;
  card.hidden = false;
  attach(card, p);

  fill(card, { title: p.title, code: p.code }, {
    creating: false,
    closed: !p.is_public
  });

  if (fillForm) {
    setForm(form, {
      title: p.title, kind: p.kind, interval_count: p.interval_count, interval_unit: p.interval_unit,
      amount: p.amount, tier: p.tier, is_public: p.is_public, is_active: p.is_active
    });
  }

  if (!p.is_public) await loadGrants();
}

function openNew() {
  opened = { creating: true };
  card.hidden = false;
  attach(card, opened);
  fill(card, {}, { creating: true, closed: false });
  form.reset();
  setForm(form, { kind: 'recurring', interval_count: 1, interval_unit: 'month', is_public: true, is_active: true });
  form.elements.code.focus();
}

function close() {
  opened = null;
  card.hidden = true;
}

async function loadGrants() {
  const p = opened;
  const grants = await api.planGrants(p.id);
  if (opened !== p) return;

  fill(card, { grantCount: grants.length });
  renderList('plan-grants', grants, function (row, g) {
    row.dataset.key = g.user_id;
    fill(row, {
      username: g.username,
      email: g.email || '—',
      term: grantTerm(g.expires_at),
      by: g.granted_by || '—',
      created: date(g.created_at)
    }, {
      expired: !!g.expires_at && new Date(g.expires_at) <= new Date()
    });
  });
}

onAction('refresh-plans', load);
onAction('new-plan', openNew);
onAction('close-plan', close);
onAction('edit-plan', ({ item }) => open(item));

onForm('plan', async function (v) {
  if (opened.creating) {
    const res = await api.createPlan(body(v, true));
    const created = (await load()).find((p) => p.id === res.id);
    if (created) await open(created);
    notify(view, 'ok', `Тариф ${res.code} создан` + (v.is_public ? '.' : ' — закрытый: откройте его нужным людям ниже.'));
    return;
  }

  const saved = await api.updatePlan(opened.id, body(v, false));
  await load();
  await open(saved);
  notify(view, 'ok', `Тариф ${saved.code} сохранён.`);
});

// Снять с продажи / вернуть. Остальные поля уходят как есть — PUT заменяет запись целиком.
onAction('toggle-plan', async function ({ item }) {
  const saved = await api.updatePlan(item.id, {
    title: item.title, tier: item.tier, kind: item.kind,
    interval_unit: item.interval_unit, interval_count: item.interval_count, amount: item.amount,
    is_public: item.is_public, is_active: !item.is_active
  });
  await load();
  notify(view, 'ok', saved.is_active ? `${saved.code} снова в продаже.` : `${saved.code} снят с продажи.`);
});

onForm('plan-grant', async function (v, grantForm) {
  const until = grantUntil(v);
  await api.grantPlan(v.username, opened.code, until);
  grantForm.reset();
  await load();
  notify(view, 'ok', `${v.username} может купить ${opened.code} ${until ? 'до ' + date(until) : 'бессрочно'}.`);
});

onAction('revoke-plan-grant', async function ({ item }) {
  await api.revokeGrant(item.username, item.plan_code);
  await load();
  notify(view, 'ok', `${item.username}: тариф ${item.plan_code} закрыт.`);
});

onAction('plan-grant-user', ({ item }) => go('users', { q: item.username, pick: item.username }));
