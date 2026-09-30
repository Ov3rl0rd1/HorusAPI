// Связка разметки и логики панели.
//
// Весь контракт между ними — data-атрибуты. Классы JS не читает и не ставит,
// поэтому вёрстку и стили можно переделывать как угодно, пока на месте
// атрибуты ниже:
//
//   data-view="servers"          раздел; видимость — атрибут hidden
//   data-show="servers"          переключатель раздела; у активного aria-current="page"
//   data-slot="name"             текст, который вписывает JS (только textContent)
//   data-list="servers"          контейнер, который JS наполняет копиями шаблона
//   <template data-template="servers">   одна строка/карточка этого списка
//   data-empty="servers"         показывается, когда список пуст
//   data-when="active"           показать, когда флаг строки истинен; "!active" — наоборот
//   data-action="evacuate"       кнопка; к какой строке относится — ближайший [data-key]
//   data-confirm="… {name} …"    спросить перед действием; {поле} берётся из строки
//   data-form="add-server"       форма; значения — по name полей, разметка внутри любая;
//                                галочка даёт "on", снятая в значения не попадает
//   data-card="user"             карточка выбранной записи; JS её показывает, прячет и
//                                привязывает к ней запись — кнопки внутри получают её поля
//   data-options="closed-plans"  <select>, чьи варианты JS пишет сам; варианты с
//                                data-fixed (например, «авто») остаются из разметки
//   data-notice                  место для сообщений своего раздела
//
// Флаги строки JS пишет на её корень как data-* ("true"/"false"), так что
// состояние можно стилизовать: [data-active="false"] { opacity: .5 }.

import { ApiError } from '/js/api.js';

export const $  = (sel, root = document) => root.querySelector(sel);
export const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

// Элемент принадлежит root, а не строке вложенного списка внутри него —
// иначе заполнение раздела перетирало бы одноимённые поля в строках.
function own(root, el) {
  return el.closest('[data-list]') === root.closest('[data-list]');
}

export function fill(root, values = {}, flags = {}) {
  for (const el of $$('[data-slot]', root)) {
    if (!own(root, el)) continue;
    const key = el.dataset.slot;
    if (key in values) el.textContent = values[key] == null ? '' : String(values[key]);
  }

  for (const [k, v] of Object.entries(flags)) root.dataset[k] = v ? 'true' : 'false';

  for (const el of $$('[data-when]', root)) {
    if (!own(root, el)) continue;
    const want = el.dataset.when;
    const neg = want.startsWith('!');
    const name = neg ? want.slice(1) : want;
    if (!(name in flags)) continue;
    el.hidden = neg ? !!flags[name] : !flags[name];
  }
}

// ── Списки ───────────────────────────────────────────────────────────────

const rowData = new WeakMap();

// Привязать данные к узлу вне списка — например, к карточке выбранной записи,
// чтобы её кнопки получали запись и подставляли её поля в data-confirm.
export function attach(node, item) {
  if (!node.hasAttribute('data-key')) node.setAttribute('data-key', '');
  rowData.set(node, item);
}

export function renderList(name, items, build) {
  const host = $(`[data-list="${name}"]`);
  const tpl  = $(`template[data-template="${name}"]`);
  if (!host || !tpl) throw new Error('В разметке нет списка или шаблона: ' + name);

  host.replaceChildren(...items.map(function (item, i) {
    const node = tpl.content.firstElementChild.cloneNode(true);
    node.setAttribute('data-key', String(i));
    attach(node, item);
    build(node, item);
    return node;
  }));

  const empty = $(`[data-empty="${name}"]`);
  if (empty) empty.hidden = items.length > 0;
}

// ── Формы и списки выбора ────────────────────────────────────────────────

// Заполнить поля формы, например, для редактирования записи. Ключи — name полей;
// чего нет в values, не трогается.
export function setForm(form, values) {
  for (const [name, value] of Object.entries(values)) {
    const el = form.elements[name];
    if (!el) continue;
    if (el.type === 'checkbox') el.checked = !!value;
    else el.value = value == null ? '' : String(value);
  }
}

// Варианты для всех <select data-options="name"> внутри root: [{ value, label, disabled }].
// Выбранное значение сохраняется, если такой вариант остался.
export function options(root, name, items) {
  for (const select of $$(`select[data-options="${name}"]`, root)) {
    const keep = select.value;
    for (const opt of Array.from(select.options)) if (!opt.hasAttribute('data-fixed')) opt.remove();
    for (const it of items) {
      const opt = new Option(it.label, String(it.value));
      opt.disabled = !!it.disabled;
      select.add(opt);
    }
    if (Array.from(select.options).some((o) => o.value === keep && !o.disabled)) select.value = keep;
    else select.selectedIndex = 0;
  }
}

// ── Диалоги ──────────────────────────────────────────────────────────────
// Единственное место, где панель что-то спрашивает. Сейчас это системные
// confirm/prompt; когда появится свой диалог, менять нужно только здесь.

export const ask = {
  confirm: (text) => Promise.resolve(window.confirm(text)),
  // null — отказ; пустая строка — согласие без значения.
  text: (label, value = '') => Promise.resolve(window.prompt(label, value))
};

function template(text, item) {
  return text.replace(/\{(\w+)\}/g, (_, k) => (item && item[k] != null ? String(item[k]) : ''));
}

// ── Сообщения ────────────────────────────────────────────────────────────

export function notify(scope, kind, text) {
  const box = scope && (scope.matches('[data-notice]') ? scope : $('[data-notice]', scope));
  if (!box) return;
  box.dataset.kind = kind;
  box.textContent = text;
  box.hidden = !text;
}

// Отказы, у которых есть понятный выход, — по-русски и с этим выходом. Остальное
// показывается как пришло от API.
const REFUSALS = {
  no_capacity:      'Свободных мест нет — ни на выбранной ноде, ни (при автовыборе) в остальном парке.',
  not_on_server:    'Пользователь уже не на этой ноде — обновите карточку.',
  same_server:      'Пользователь уже на этой ноде.',
  target_inactive:  'Нода назначения выведена из ротации.',
  target_not_found: 'Ноды назначения нет — обновите список.',
  plan_exists:      'Тариф с таким кодом уже есть (регистр букв не различается).',
  plan_in_use:      'По тарифу есть ожидающие оплаты или действующие подписки: тип и период менять нельзя. ' +
                    'Создайте новый тариф, а этот снимите с продажи.'
};

export function errorText(err) {
  if (err instanceof ApiError) {
    const body = err.body || {};
    return REFUSALS[err.code] || err.message || body.detail || body.title || ('Ошибка ' + (err.status || 'сети'));
  }
  return String((err && err.message) || err);
}

// Панель открывается только после проверки на входе, поэтому 401/403 здесь —
// это сессия, отозванная или истёкшая уже во время работы.
let onAuthLost = function () {};
export function whenAuthLost(fn) { onAuthLost = fn; }

export function report(scope, err) {
  if (err instanceof ApiError && (err.status === 401 || err.status === 403)) onAuthLost();
  else if (scope) notify(scope, 'error', errorText(err));
  else console.error(err);
}

// Действие с блокировкой кнопки и сообщением об ошибке в своём разделе.
export async function run(trigger, fn) {
  const scope = trigger && trigger.closest('[data-view]');
  if (trigger) { trigger.disabled = true; trigger.setAttribute('aria-busy', 'true'); }
  try {
    if (scope) notify(scope, '', '');
    return await fn();
  } catch (err) {
    report(scope, err);
  } finally {
    if (trigger) { trigger.disabled = false; trigger.removeAttribute('aria-busy'); }
  }
}

// ── Действия и формы ─────────────────────────────────────────────────────

const actions = {};
const forms = {};

export function onAction(name, handler) { actions[name] = handler; }
export function onForm(name, handler) { forms[name] = handler; }

document.addEventListener('click', async function (e) {
  const btn = e.target.closest('[data-action]');
  if (!btn || !actions[btn.dataset.action]) return;
  e.preventDefault();

  const row  = btn.closest('[data-key]');
  const item = row ? rowData.get(row) : undefined;

  if (btn.dataset.confirm && !(await ask.confirm(template(btn.dataset.confirm, item)))) return;
  run(btn, () => actions[btn.dataset.action]({ item, row, button: btn }));
});

document.addEventListener('submit', function (e) {
  const form = e.target.closest('[data-form]');
  if (!form || !forms[form.dataset.form]) return;
  e.preventDefault();

  const values = {};
  for (const [k, v] of new FormData(form)) values[k] = typeof v === 'string' ? v.trim() : v;
  run(e.submitter || $('[type="submit"]', form), () => forms[form.dataset.form](values, form));
});

// Переход в другой раздел с параметрами — например, из пользователя в его платежи.
export function go(view, params) {
  document.dispatchEvent(new CustomEvent('panel:go', { detail: { view, params: params || {} } }));
}

// ── Значения ─────────────────────────────────────────────────────────────

export function dateTime(value) {
  if (!value) return '—';
  return new Date(value).toLocaleString('ru-RU', {
    day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'
  });
}

export function date(value) {
  if (!value) return '—';
  return new Date(value).toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit', year: 'numeric' });
}

export function ago(value) {
  if (!value) return 'никогда';
  const s = Math.round((Date.now() - new Date(value).getTime()) / 1000);
  if (s < 60) return s + ' с назад';
  if (s < 3600) return Math.round(s / 60) + ' мин назад';
  if (s < 86400) return Math.round(s / 3600) + ' ч назад';
  return Math.round(s / 86400) + ' дн назад';
}

// <input type="date"> даёт день без времени. Доступ «до 31.12» значит до конца
// этого дня по часам того, кто его выдаёт, а не до полуночи UTC.
export function endOfDay(value) {
  return value ? new Date(value + 'T23:59:59').toISOString() : null;
}

export function intOrNull(value) {
  if (value === '' || value == null) return null;
  const n = parseInt(value, 10);
  return Number.isFinite(n) ? n : null;
}

export const orNull = (value) => (value === '' || value == null ? null : value);

// Срок доступа к закрытому тарифу из формы: галочка «бессрочно» важнее даты.
// null — бессрочно; без галочки дата обязательна, иначе ошибка, а не молчаливое «навсегда».
export function grantUntil(v) {
  if (v.forever) return null;
  if (!v.until) throw new Error('Укажите дату или отметьте «бессрочно».');
  return endOfDay(v.until);
}

export function grantTerm(expiresAt) {
  if (!expiresAt) return 'бессрочно';
  const end = new Date(expiresAt);
  return (end > new Date() ? 'до ' : 'истёк ') + date(end);
}

const UNITS = { day: 'дн', week: 'нед', month: 'мес', year: 'г' };

// «1 мес», «3 мес», «1 г», «7 дн».
export function period(unit, count) {
  return count + ' ' + (UNITS[unit] || unit);
}
