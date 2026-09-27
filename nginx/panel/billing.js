// Разделы «Платежи» и «Промокоды».

import * as api from './admin-api.js';
import { $, fill, renderList, onAction, onForm, notify, ask, go, dateTime, date, endOfDay, intOrNull, orNull } from './bind.js';

// ── Платежи ──────────────────────────────────────────────────────────────

const payView = $('[data-view="payments"]');
const payUser = $('[data-form="payment-search"] [name="user"]', payView);

const STATUS = {
  pending: 'ожидает оплаты',
  confirmed: 'оплачен',
  failed: 'не прошёл',
  canceled: 'отменён',
  refunded: 'возвращён',
  chargebacked: 'чарджбэк'
};

export async function loadPayments(params) {
  if (params && typeof params.user === 'string') payUser.value = params.user;
  await listPayments(payUser.value.trim());
}

async function listPayments(user) {
  const rows = await api.payments(user);
  fill(payView, { paymentsCount: rows.length, paymentsTotal: sum(rows) });

  renderList('payments', rows, function (row, p) {
    row.dataset.key = p.id;
    fill(row, {
      id: p.id,
      created: dateTime(p.created_at),
      user: p.username,
      userId: p.user_id,
      plan: p.plan_code || '—',
      kind: p.kind === 'recurring' ? 'подписка' : 'разовый',
      amount: p.amount + ' ₽',
      discount: p.discount ? '−' + p.discount + ' ₽' : '',
      status: STATUS[p.status] || p.status,
      ref: p.provider_ref || ''
    }, {
      confirmed: p.status === 'confirmed',
      discounted: p.discount > 0
    });
    row.dataset.status = p.status;
  });
}

// Сумма только оплаченных: ожидающие и отменённые деньгами не были.
function sum(rows) {
  return rows.filter((p) => p.status === 'confirmed').reduce((a, p) => a + p.amount, 0) + ' ₽';
}

onForm('payment-search', (v) => listPayments(v.user || ''));
onAction('payment-user', ({ item }) => go('users', { q: item.username }));

// Возврат отзывает доступ. Пустая сумма — вернуть всё.
onAction('refund', async function ({ item }) {
  const amount = await ask.text(`Сумма возврата по платежу #${item.id}, ₽. Пусто — всё (${item.amount} ₽).`, '');
  if (amount === null) return;
  const reason = await ask.text('Причина (увидит только поддержка):', '');
  if (reason === null) return;

  const sumText = (intOrNull(amount) || item.amount) + ' ₽';
  if (!(await ask.confirm(`Вернуть ${sumText} пользователю ${item.username} по платежу #${item.id}? Доступ будет отозван.`))) return;

  const r = await api.refund(item.id, intOrNull(amount), orNull(reason.trim()));
  await listPayments(payUser.value.trim());
  notify(payView, r.status === 'refunded' ? 'ok' : 'warn',
    r.status === 'refunded'
      ? `Платёж #${item.id} возвращён, доступ отозван.`
      : `Платёж #${item.id}: провайдер не вернёт автоматически — нужен ручной возврат. ${r.detail || ''}`);
});

// ── Промокоды ────────────────────────────────────────────────────────────

const promoView = $('[data-view="promos"]');

export async function loadPromos() {
  const rows = await api.promos();
  renderList('promos', rows, function (row, p) {
    row.dataset.key = p.id;
    fill(row, {
      code: p.code,
      percent: p.percent_off + '%',
      used: p.redeemed_count + (p.max_redemptions ? ' / ' + p.max_redemptions : ''),
      perUser: p.per_user_limit || '—',
      plan: p.plan_id ? '#' + p.plan_id : 'любой',
      period: (p.starts_at || p.ends_at) ? date(p.starts_at) + ' — ' + date(p.ends_at) : 'без срока'
    }, {
      active: p.is_active
    });
  });
}

onAction('deactivate-promo', async function ({ item }) {
  await api.deactivatePromo(item.code);
  await loadPromos();
  notify(promoView, 'ok', `Промокод ${item.code} выключен.`);
});

onForm('add-promo', async function (v, form) {
  await api.createPromo({
    code: v.code,
    percent_off: intOrNull(v.percent_off),
    max_redemptions: intOrNull(v.max_redemptions),
    per_user_limit: intOrNull(v.per_user_limit),
    plan_code: orNull(v.plan_code),
    starts_at: v.starts_at ? new Date(v.starts_at + 'T00:00:00').toISOString() : null,
    ends_at: endOfDay(v.ends_at)
  });
  form.reset();
  await loadPromos();
  notify(promoView, 'ok', `Промокод ${v.code} создан. Действует только на разовые покупки.`);
});
