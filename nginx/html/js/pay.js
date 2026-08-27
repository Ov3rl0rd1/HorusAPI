// Страница подписки и оплаты. Тянет доступные тарифы, создаёт платёж и уводит на
// форму провайдера. Доступ выдаёт вебхук, а не редирект, поэтому после возврата
// (?paid=1) статус подписки опрашивается, пока не станет активным.

import { billingPlans, billingCheckout, billingSubscription, billingCancel } from './endpoints.js';
import { requireSession, clearSession } from './session.js';
import { byId, queryParam, plural } from './util.js';

const states = {
  loading: byId('state-loading'),
  error: byId('state-error'),
  empty: byId('state-empty'),
  plans: byId('state-plans')
};
function show(name) {
  Object.keys(states).forEach(function (k) { states[k].classList.toggle('is-active', k === name); });
}

const DAY = 86400000;
const sleep = function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };
function toLogin() { clearSession(); location.replace('/login'); }

// ── Формат тарифа ─────────────────────────────────────────────────────────
const UNIT_WORDS = {
  day:   ['день', 'дня', 'дней'],
  week:  ['неделю', 'недели', 'недель'],
  month: ['месяц', 'месяца', 'месяцев'],
  year:  ['год', 'года', 'лет']
};
// Во сколько месяцев обходится период — только для «≈ N ₽ / мес» и скидки.
const IN_MONTHS = { day: 1 / 30, week: 7 / 30, month: 1, year: 12 };

function count(p) { return Number(p.interval_count) || 1; }
function amount(p) { return Number(p.amount) || 0; }
function months(p) { return count(p) * (IN_MONTHS[p.interval_unit] || 1); }
function isRecurring(p) { return p.kind === 'recurring'; }

function periodLabel(p) {
  const forms = UNIT_WORDS[p.interval_unit] || UNIT_WORDS.month;
  return count(p) + ' ' + plural(count(p), forms);
}
function money(value, currency) {
  const sum = Math.round(Number(value) || 0).toLocaleString('ru-RU');
  return currency && currency !== 'RUB' ? sum + ' ' + currency : sum + ' ₽';
}
function planTitle(p) { return p.title || p.code; }
function planTerms(p) {
  return isRecurring(p)
    ? 'списание каждые ' + periodLabel(p)
    : 'разово · ' + periodLabel(p) + ' доступа';
}

// ── Тарифы ────────────────────────────────────────────────────────────────
let allPlans = [];
let kind = 'recurring';      // какая вкладка открыта
let selected = '';           // code выбранного тарифа

function group(k) {
  return allPlans.filter(function (p) { return k === 'recurring' ? isRecurring(p) : !isRecurring(p); });
}

// Скидка считается от самого дорогого месяца в группе — это и есть «выгода».
function savings(p, list) {
  const perMonth = amount(p) / (months(p) || 1);
  const base = Math.max.apply(null, list.map(function (x) { return amount(x) / (months(x) || 1); }));
  if (!base || !isFinite(base)) return 0;
  return Math.round((1 - perMonth / base) * 100);
}

function planCard(p, list) {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'plan' + (p.code === selected ? ' is-on' : '');
  btn.setAttribute('role', 'radio');
  btn.setAttribute('aria-checked', p.code === selected ? 'true' : 'false');

  const title = document.createElement('div');
  title.className = 'plan__title';
  title.textContent = planTitle(p);

  const price = document.createElement('div');
  price.className = 'plan__price';
  price.textContent = money(amount(p), p.currency);

  const per = document.createElement('div');
  per.className = 'plan__per';
  per.textContent = isRecurring(p) ? 'каждые ' + periodLabel(p) : 'за ' + periodLabel(p);

  btn.appendChild(title);
  btn.appendChild(price);
  btn.appendChild(per);

  if (months(p) > 1.2) {
    const permonth = document.createElement('div');
    permonth.className = 'plan__month';
    permonth.textContent = '≈ ' + money(amount(p) / months(p), p.currency) + ' в месяц';
    btn.appendChild(permonth);
  }

  const save = savings(p, list);
  if (save >= 5) {
    const tag = document.createElement('div');
    tag.className = 'plan__save';
    tag.textContent = 'Выгоднее на ' + save + '%';
    btn.appendChild(tag);
  }

  if (p.is_public === false) {
    const tag = document.createElement('div');
    tag.className = 'plan__tag';
    tag.textContent = 'Персональный тариф';
    btn.appendChild(tag);
  }

  btn.addEventListener('click', function () { selected = p.code; renderGroup(); });
  return btn;
}

function renderSummary() {
  const p = allPlans.filter(function (x) { return x.code === selected; })[0];
  const line = byId('sum-line'), total = byId('sum-total');
  byId('pay-btn').disabled = !p;
  if (!p) {
    line.textContent = 'Выберите тариф';
    total.textContent = '—';
    return;
  }
  line.textContent = planTitle(p) + ' · ' + planTerms(p);
  total.textContent = money(amount(p), p.currency);
}

function renderGroup() {
  const list = group(kind);
  if (!list.filter(function (p) { return p.code === selected; }).length) {
    selected = list.length ? list[0].code : '';
  }

  const host = byId('plan-list');
  host.textContent = '';
  list.forEach(function (p) { host.appendChild(planCard(p, list)); });

  byId('kind-note').textContent = kind === 'recurring'
    ? 'Списывается автоматически каждый период. Автопродление можно отключить в любой момент — доступ доработает до конца оплаченного срока.'
    : 'Один платёж за выбранный срок. Ничего не списывается автоматически, продлевать нужно вручную.';

  byId('promo-wrap').hidden = kind === 'recurring';
  byId('kind-recurring').classList.toggle('is-on', kind === 'recurring');
  byId('kind-onetime').classList.toggle('is-on', kind === 'onetime');
  byId('kind-recurring').setAttribute('aria-selected', String(kind === 'recurring'));
  byId('kind-onetime').setAttribute('aria-selected', String(kind === 'onetime'));

  byId('pay-msg').className = 'msg msg--error';
  renderSummary();
}

function switchKind(next) {
  if (kind === next) return;
  kind = next;
  selected = '';
  renderGroup();
}

async function loadPlans() {
  show('loading');
  try {
    const plans = await billingPlans();
    allPlans = (plans || []).slice().sort(function (a, b) { return months(a) - months(b); });
    if (!allPlans.length) { show('empty'); return; }

    const hasRecurring = group('recurring').length > 0;
    const hasOnetime = group('onetime').length > 0;
    kind = hasRecurring ? 'recurring' : 'onetime';
    byId('kind-seg').hidden = !(hasRecurring && hasOnetime);

    renderGroup();
    show('plans');
  } catch (err) {
    if (err.isAuth) { toLogin(); return; }
    byId('error-text').textContent = err.isNetwork
      ? 'Сеть недоступна. Проверьте соединение и попробуйте ещё раз.'
      : (err.message || 'Сервер ответил ошибкой. Попробуйте позже.');
    show('error');
  }
}

// ── Текущая подписка ──────────────────────────────────────────────────────
function subView(s) {
  const status = (s && s.status) || 'none';
  const end = s && s.current_period_end ? new Date(s.current_period_end) : null;
  const date = end ? end.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' }) : '';
  const left = end ? end.getTime() - Date.now() : 0;
  const days = end ? Math.ceil(left / DAY) : 0;
  const plan = s && s.plan_code ? 'Тариф ' + s.plan_code : '';
  const parts = function (list) { return list.filter(Boolean).join(' · '); };

  switch (status) {
    case 'active':
      return {
        badge: ['badge--ok', 'Активна'],
        state: end && left > 0 ? 'Активна ещё ' + days + ' ' + plural(days, ['день', 'дня', 'дней']) : 'Активна',
        note: parts([plan, date ? 'оплачено до ' + date : '',
          s.cancel_at_period_end ? 'автопродление отключено' : (s.kind === 'recurring' ? 'продлится автоматически' : 'продлевать вручную')]),
        connect: true,
        cancel: s.kind === 'recurring' && !s.cancel_at_period_end
      };
    case 'comp':
      return { badge: ['badge--ok', 'Служебный доступ'], state: 'Служебный доступ',
        note: parts([plan, date ? 'до ' + date : 'без ограничения по сроку']), connect: true, cancel: false };
    case 'past_due':
      return { badge: ['badge--warn', 'Платёж не прошёл'], state: 'Просрочен платёж',
        note: parts([plan, date ? 'доступ сохранится до ' + date : '', 'оплатите, чтобы не потерять место на сервере']),
        connect: true, cancel: false };
    case 'pending':
      return { badge: ['badge--idle', 'Ожидает оплаты'], state: 'Ожидаем платёж',
        note: parts([plan, 'подписка включится, как только банк подтвердит оплату']), connect: false, cancel: false };
    case 'canceled':
      return { badge: ['badge--idle', 'Отменена'], state: left > 0 ? 'Отменена, действует до конца срока' : 'Подписка закончилась',
        note: parts([plan, date ? (left > 0 ? 'доступ до ' + date : 'срок закончился ' + date) : '']),
        connect: left > 0, cancel: false };
    case 'failed':
      return { badge: ['badge--warn', 'Не активирована'], state: 'Оплата не завершена',
        note: 'Платёж не прошёл. Выберите тариф ниже и попробуйте снова.', connect: false, cancel: false };
    default:
      return { badge: ['badge--idle', 'Подписки нет'], state: 'Подписка не оформлена',
        note: 'Выберите тариф ниже — доступ включится сразу после оплаты.', connect: false, cancel: false };
  }
}

function renderSub(s) {
  const view = subView(s);

  const host = byId('sub-badge');
  host.textContent = '';
  const badge = document.createElement('span');
  badge.className = 'badge ' + view.badge[0];
  badge.textContent = view.badge[1];
  host.appendChild(badge);
  host.style.marginBottom = '12px';

  byId('sub-state').textContent = view.state;
  byId('sub-note').textContent = view.note;
  byId('sub-connect').hidden = !view.connect;
  byId('cancel-btn').hidden = !view.cancel;
  byId('sub-actions').hidden = !view.connect && !view.cancel;
}

async function refreshSub() {
  try {
    const s = await billingSubscription();
    renderSub(s);
    return s;
  } catch (err) {
    if (err.isAuth) { toLogin(); return null; }
    renderSub(null);
    return null;
  }
}

async function onCancel() {
  const ok = window.confirm('Отключить автопродление? Доступ сохранится до конца оплаченного периода, дальше списаний не будет.');
  if (!ok) return;
  const btn = byId('cancel-btn'), msg = byId('sub-msg');
  btn.disabled = true;
  btn.textContent = 'Отключаем…';
  msg.className = 'msg';
  try {
    await billingCancel();
    await refreshSub();
    msg.textContent = 'Автопродление отключено. Доступ работает до конца оплаченного периода.';
    msg.className = 'msg msg--ok is-shown';
  } catch (err) {
    if (err.isAuth) { toLogin(); return; }
    msg.textContent = err.code === 'no_subscription'
      ? 'Активной подписки нет.'
      : (err.isNetwork ? 'Сеть недоступна. Попробуйте ещё раз.' : (err.message || 'Не удалось отключить автопродление.'));
    msg.className = 'msg msg--error is-shown';
  } finally {
    btn.disabled = false;
    btn.textContent = btn.getAttribute('data-label');
  }
}

// ── Оплата ────────────────────────────────────────────────────────────────
function checkoutError(err) {
  switch (err.code) {
    case 'no_capacity':          return 'Свободных мест на серверах сейчас нет. Попробуйте позже или напишите в поддержку.';
    case 'promo_not_applicable': return 'Промокод действует только для разовой оплаты.';
    case 'promo_invalid':        return 'Промокод не найден или больше не действует.';
    case 'plan_not_found':       return 'Тариф больше не доступен. Обновите страницу.';
    case 'provider_error':       return 'Платёжный сервис не отвечает. Попробуйте через пару минут.';
    default:                     return err.isNetwork
      ? 'Сеть недоступна. Попробуйте ещё раз.'
      : (err.message || 'Не удалось создать платёж.');
  }
}

async function onPay() {
  if (!selected) return;
  const btn = byId('pay-btn'), msg = byId('pay-msg');
  const promo = kind === 'onetime' ? byId('promo').value.trim() : '';
  msg.className = 'msg msg--error';
  btn.disabled = true;
  btn.textContent = 'Готовим оплату…';
  try {
    const res = await billingCheckout(selected, promo);
    if (res && res.redirect) { location.href = res.redirect; return; }
    msg.textContent = 'Платёжный сервис не вернул ссылку на оплату. Попробуйте ещё раз.';
    msg.className = 'msg msg--error is-shown';
  } catch (err) {
    if (err.isAuth) { toLogin(); return; }
    msg.textContent = checkoutError(err);
    msg.className = 'msg msg--error is-shown';
  } finally {
    btn.disabled = !selected;
    btn.textContent = btn.getAttribute('data-label');
  }
}

// ── Возврат с формы провайдера ────────────────────────────────────────────
// Подписку активирует вебхук, поэтому после ?paid=1 опрашиваем статус.
async function handleReturn() {
  const paid = queryParam('paid');
  if (!paid) return;
  const msg = byId('sub-msg');

  if (paid === '0') {
    msg.textContent = 'Оплата не завершена — деньги не списаны. Можно попробовать снова.';
    msg.className = 'msg msg--error is-shown';
    return;
  }

  msg.textContent = 'Оплата отправлена, ждём подтверждения банка…';
  msg.className = 'msg is-shown';
  for (let i = 0; i < 10; i++) {
    const s = await refreshSub();
    if (s && (s.status === 'active' || s.status === 'comp')) {
      msg.textContent = 'Оплата прошла, подписка активна. Можно подключаться.';
      msg.className = 'msg msg--ok is-shown';
      return;
    }
    await sleep(2000);
  }
  msg.textContent = 'Платёж ещё обрабатывается. Обновите страницу через минуту — если статус не изменится, напишите в поддержку.';
  msg.className = 'msg is-shown';
}

// ── Старт ─────────────────────────────────────────────────────────────────
async function start() {
  byId('logout').addEventListener('click', function () { toLogin(); });
  byId('retry').addEventListener('click', loadPlans);
  byId('pay-btn').addEventListener('click', onPay);
  byId('cancel-btn').addEventListener('click', onCancel);
  byId('kind-recurring').addEventListener('click', function () { switchKind('recurring'); });
  byId('kind-onetime').addEventListener('click', function () { switchKind('onetime'); });

  await refreshSub();
  await loadPlans();
  await handleReturn();
}

// Как в cabinet.js: start() только после всех объявлений, иначе states и функции в TDZ.
if (requireSession('/login')) start();
