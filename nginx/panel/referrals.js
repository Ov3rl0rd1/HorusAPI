// Раздел «Партнёры»: реферальная программа. Партнёра назначает только админ —
// самостоятельной записи нет. Деньги — целые рубли, как везде в API.

import * as api from './admin-api.js';
import {
  $, fill, attach, renderList, onAction, onForm, notify, go, setForm, dateTime, date, intOrNull, orNull
} from './bind.js';

const view = $('[data-view="referrals"]');
const card = $('[data-card="referral"]', view);
const form = $('[data-form="referral"]', card);

// Открытая карточка: ReferralPartnerDetail, или { creating: true } для нового партнёра.
let opened = null;

const rub = (n) => (n || 0).toLocaleString('ru-RU') + ' ₽';

// params.username — открыть этого пользователя: его карточку партнёра, а если он
// ещё не партнёр — форму создания с подставленным именем (переход из «Пользователей»).
export async function load(params) {
  const list = await api.referrals();

  renderList('referrals', list, function (row, p) {
    row.dataset.key = p.user_id;
    fill(row, {
      username: p.username,
      code: p.code,
      discount: p.discount_percent + '%',
      reward: p.reward_percent + '%',
      invited: p.invited,
      paying: p.paying,
      revenue: rub(p.revenue),
      earned: rub(p.earned),
      paidOut: rub(p.paid_out),
      balance: rub(p.balance)
    }, {
      active: p.is_active
    });
  });

  if (params && params.username) {
    const known = list.find((p) => p.username === params.username);
    if (known) await open(known.username);
    else openNew(params.username);
  } else if (opened && !opened.creating) {
    // Карточка обновляется вместе со списком, но без перезаписи недописанной формы.
    if (list.some((p) => p.username === opened.partner.username)) await open(opened.partner.username, false);
    else close();
  }
  return list;
}

async function open(username, fillForm = true) {
  const d = await api.referral(username);
  opened = d;
  const p = d.partner;

  card.hidden = false;
  attach(card, p);
  fill(card, {
    username: p.username,
    code: p.code,
    link: d.link,
    invited: p.invited,
    invitedCount: d.invited.length,
    paying: p.paying,
    revenue: rub(p.revenue),
    earned: rub(p.earned),
    paidOut: rub(p.paid_out),
    balance: rub(p.balance)
  }, {
    creating: false,
    active: p.is_active
  });

  if (fillForm) {
    form.reset();
    setForm(form, {
      code: p.code,
      discount_percent: p.discount_percent,
      reward_percent: p.reward_percent,
      note: p.note,
      is_active: p.is_active
    });
  }

  renderList('referral-invited', d.invited, function (row, c) {
    row.dataset.key = c.user_id;
    fill(row, {
      username: c.username,
      since: date(c.referred_at || c.created_at),
      paid: rub(c.paid)
    }, {
      access: c.has_access
    });
  });

  renderList('referral-rewards', d.rewards, function (row, r) {
    row.dataset.key = r.id;
    fill(row, {
      created: dateTime(r.created_at),
      username: r.username || '—',
      paid: rub(r.paid_amount),
      percent: r.percent + '%',
      amount: rub(r.amount)
    }, {
      reversed: r.status === 'reversed'
    });
  });

  renderList('referral-payouts', d.payouts, function (row, x) {
    row.dataset.key = x.id;
    fill(row, {
      created: dateTime(x.created_at),
      amount: rub(x.amount),
      note: x.note || '—',
      by: x.created_by || '—'
    });
  });
}

function openNew(username) {
  opened = { creating: true };
  card.hidden = false;
  attach(card, opened);
  fill(card, {}, { creating: true, active: true });
  form.reset();
  setForm(form, { username: username || '', discount_percent: 10, reward_percent: 30, is_active: true });
  (username ? form.elements.code : form.elements.username).focus();
}

function close() {
  opened = null;
  card.hidden = true;
}

onAction('refresh-referrals', () => load());
onAction('new-referral', () => openNew(''));
onAction('close-referral', close);
onAction('open-referral', ({ item }) => open(item.username));
onAction('referral-user', ({ item }) => go('users', { q: item.username, pick: item.username }));

onAction('copy-referral-link', async function () {
  if (!opened || opened.creating) return;
  await navigator.clipboard.writeText(opened.link);
  notify(view, 'ok', 'Ссылка скопирована: ' + opened.link);
});

onForm('referral', async function (v) {
  const body = {
    code: orNull(v.code),
    discount_percent: intOrNull(v.discount_percent),
    reward_percent: intOrNull(v.reward_percent),
    is_active: !!v.is_active,
    note: v.note || ''          // пустая строка стирает заметку, null — оставил бы прежнюю
  };

  if (opened.creating) {
    if (!v.username) throw new Error('Укажите пользователя.');
    if (!body.code) throw new Error('Укажите код.');
    const saved = await api.saveReferral(v.username, body);
    await load();
    await open(saved.username);
    notify(view, 'ok', `${saved.username} — партнёр, код ${saved.code}. Ссылку можно скопировать в карточке.`);
    return;
  }

  const saved = await api.saveReferral(opened.partner.username, body);
  await load();
  await open(saved.username);
  notify(view, 'ok', `Партнёр ${saved.username} сохранён.`);
});

onAction('toggle-referral', async function ({ item }) {
  const saved = await api.saveReferral(item.username, { is_active: !item.is_active });
  await load();
  notify(view, 'ok', saved.is_active ? `Партнёр ${saved.username} включён.` : `Партнёр ${saved.username} выключен.`);
});

onForm('referral-payout', async function (v, payoutForm) {
  const amount = intOrNull(v.amount);
  if (!amount) throw new Error('Укажите сумму в рублях.');
  const saved = await api.referralPayout(opened.partner.username, amount, orNull(v.note));
  payoutForm.reset();
  await load();
  await open(saved.username);
  notify(view, 'ok', `Записана выплата ${rub(amount)} партнёру ${saved.username}. Остаток: ${rub(saved.balance)}.`);
});
