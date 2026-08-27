// Временная тестовая страница оплаты. Тянет доступные тарифы, создаёт checkout и
// уводит на форму провайдера; после возврата (?paid=1) опрашивает статус подписки,
// пока вебхук её не активирует. Дизайн намеренно минимальный — важен только прогон.

import { billingPlans, billingCheckout, billingSubscription, billingCancel } from './endpoints.js';
import { requireSession, clearSession } from './session.js';
import { byId, plural, queryParam } from './util.js';

const states = { loading: byId('state-loading'), empty: byId('state-empty'), plans: byId('state-plans') };
function show(name) {
  Object.keys(states).forEach(function (k) { states[k].classList.toggle('is-active', k === name); });
}

const sleep = function (ms) { return new Promise(function (r) { setTimeout(r, ms); }); };

// ── Ярлыки тарифа ───────────────────────────────────────────────────────────
const UNIT_WORDS = {
  day:   ['день', 'дня', 'дней'],
  week:  ['неделя', 'недели', 'недель'],
  month: ['месяц', 'месяца', 'месяцев'],
  year:  ['год', 'года', 'лет']
};

function intervalLabel(p) {
  const forms = UNIT_WORDS[p.interval_unit] || UNIT_WORDS.month;
  const n = Number(p.interval_count) || 1;
  return n + ' ' + plural(n, forms);
}
function kindLabel(p) {
  return p.kind === 'recurring'
    ? 'подписка · списание каждые ' + intervalLabel(p)
    : 'разовый платёж · ' + intervalLabel(p) + ' доступа';
}
function priceLabel(p) { return (Number(p.amount) || 0) + ' ₽'; }

// ── Подписка ─────────────────────────────────────────────────────────────────
function statusLabel(status) {
  switch (status) {
    case 'active':   return 'Активна';
    case 'comp':     return 'Служебный доступ';
    case 'past_due': return 'Просрочен платёж (доступ до конца периода)';
    case 'canceled': return 'Отменена';
    case 'pending':  return 'Ожидает оплаты';
    case 'failed':   return 'Не активирована';
    default:         return 'Нет';
  }
}

function renderSub(s) {
  const state = byId('sub-state'), note = byId('sub-note'), cancel = byId('cancel-btn');
  if (!s || !s.status || s.status === 'none') {
    state.textContent = 'Подписки нет';
    note.textContent = 'Выберите тариф ниже и оплатите.';
    cancel.hidden = true;
    return;
  }
  state.textContent = statusLabel(s.status);
  const end = s.current_period_end ? new Date(s.current_period_end) : null;
  const parts = [];
  if (s.plan_code) parts.push('Тариф: ' + s.plan_code);
  if (end) parts.push('до ' + end.toLocaleString('ru-RU', { day: 'numeric', month: 'long', year: 'numeric' }));
  if (s.cancel_at_period_end) parts.push('автопродление отключено');
  note.textContent = parts.join(' · ');

  cancel.hidden = !(s.kind === 'recurring' && (s.status === 'active' || s.status === 'past_due') && !s.cancel_at_period_end);
}

async function refreshSubscription() {
  try {
    const s = await billingSubscription();
    renderSub(s);
    return s;
  } catch (err) {
    if (err.isAuth) { clearSession(); location.replace('/login'); return null; }
    return null;
  }
}

// ── Тарифы ─────────────────────────────────────────────────────────────────────
function renderPlans(plans) {
  const host = byId('plan-list');
  host.textContent = '';
  plans.forEach(function (p, i) {
    const label = document.createElement('label');
    label.className = 'srv';
    label.style.cursor = 'pointer';

    const radio = document.createElement('input');
    radio.type = 'radio';
    radio.name = 'plan';
    radio.value = p.code;
    radio.style.marginRight = '12px';
    if (i === 0) radio.checked = true;
    radio.addEventListener('change', function () { byId('pay-btn').disabled = false; });

    const place = document.createElement('div');
    place.className = 'srv__place';
    const name = document.createElement('div');
    name.className = 'srv__name';
    name.textContent = (p.title || p.code) + (p.is_public ? '' : ' · для своих');
    const where = document.createElement('div');
    where.className = 'srv__where';
    where.textContent = priceLabel(p) + ' · ' + kindLabel(p);
    place.appendChild(name);
    place.appendChild(where);

    label.appendChild(radio);
    label.appendChild(place);
    host.appendChild(label);
  });
  byId('pay-btn').disabled = plans.length === 0;
}

function selectedPlan() {
  const el = document.querySelector('input[name="plan"]:checked');
  return el ? el.value : '';
}

async function loadPlans() {
  show('loading');
  try {
    const plans = await billingPlans();
    if (!plans || !plans.length) { show('empty'); return; }
    renderPlans(plans);
    show('plans');
  } catch (err) {
    if (err.isAuth) { clearSession(); location.replace('/login'); return; }
    show('empty');
    byId('state-empty').querySelector('p').textContent =
      'Не удалось загрузить тарифы: ' + (err.message || 'ошибка сервера') + '.';
  }
}

// ── Оплата ─────────────────────────────────────────────────────────────────────
function checkoutError(err) {
  switch (err.code) {
    case 'no_capacity':          return 'Нет свободных мест на серверах. Попробуйте позже.';
    case 'promo_not_applicable': return 'Промокод не применяется к подпискам (только к разовым платежам).';
    case 'promo_invalid':        return 'Промокод недействителен.';
    case 'plan_not_found':       return 'Тариф недоступен.';
    case 'provider_error':       return 'Платёжный провайдер недоступен. Попробуйте позже.';
    default:                     return err.isNetwork ? 'Сеть недоступна. Попробуйте ещё раз.' : (err.message || 'Не удалось создать платёж.');
  }
}

async function onPay() {
  const code = selectedPlan();
  if (!code) return;
  const promo = byId('promo').value.trim();
  const btn = byId('pay-btn'), msg = byId('pay-msg');
  msg.textContent = ''; msg.className = 'msg msg--error';
  btn.disabled = true; btn.textContent = 'Создаём платёж…';
  try {
    const res = await billingCheckout(code, promo);
    if (res && res.redirect) { location.href = res.redirect; return; }   // → форма провайдера
    msg.textContent = 'Провайдер не вернул ссылку на оплату.'; msg.className = 'msg msg--error is-shown';
  } catch (err) {
    if (err.isAuth) { clearSession(); location.replace('/login'); return; }
    msg.textContent = checkoutError(err); msg.className = 'msg msg--error is-shown';
  } finally {
    btn.disabled = false; btn.textContent = btn.getAttribute('data-label');
  }
}

async function onCancel() {
  const btn = byId('cancel-btn');
  btn.disabled = true; btn.textContent = 'Отменяем…';
  try { await billingCancel(); await refreshSubscription(); }
  catch (err) { if (err.isAuth) { clearSession(); location.replace('/login'); return; } }
  finally { btn.disabled = false; btn.textContent = btn.getAttribute('data-label'); }
}

// ── Возврат с формы провайдера ───────────────────────────────────────────────
// Доступ выдаёт ВЕБХУК, не редирект, поэтому после ?paid=1 опрашиваем статус.
async function handleReturn() {
  const paid = queryParam('paid');
  if (!paid) return;
  const msg = byId('return-msg');
  if (paid === '0') { msg.textContent = 'Оплата не завершена. Можно попробовать снова.'; msg.className = 'msg msg--error is-shown'; return; }

  msg.textContent = 'Оплата отправлена. Ждём подтверждения от банка…'; msg.className = 'msg is-shown';
  for (let i = 0; i < 10; i++) {
    const s = await refreshSubscription();
    if (s && (s.status === 'active' || s.status === 'comp')) {
      msg.textContent = 'Оплата прошла, подписка активна ✓'; msg.className = 'msg msg--ok is-shown';
      return;
    }
    await sleep(2000);
  }
  msg.textContent = 'Платёж ещё обрабатывается. Обновите страницу через минуту.'; msg.className = 'msg is-shown';
}

// ── Старт ────────────────────────────────────────────────────────────────────
async function start() {
  byId('logout').addEventListener('click', function () { clearSession(); location.replace('/login'); });
  byId('pay-btn').addEventListener('click', onPay);
  byId('cancel-btn').addEventListener('click', onCancel);

  await refreshSubscription();
  await loadPlans();
  await handleReturn();
}

// Как в cabinet.js: start() только после всех объявлений (иначе states/функции в TDZ).
if (requireSession('/login')) start();
