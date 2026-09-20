import { login, register, verifyEmail, resendCode, changeEmail, requestPasswordReset } from './endpoints.js';
import { saveSession, isSignedIn } from './session.js';
import { byId, qsa, queryParam } from './util.js';

const AFTER_LOGIN = '/cabinet';   // личный кабинет
const MIN_LENGTH = 8;
const RESEND_COOLDOWN = 60;           // запасной кулдаун, если сервер не назвал свой

if (isSignedIn()) location.replace(AFTER_LOGIN);

const states = {
  login: byId('state-login'),
  register: byId('state-register'),
  verify: byId('state-verify'),
  verifyExpired: byId('state-verify-expired'),
  forgot: byId('state-forgot'),
  sent: byId('state-sent')
};
const tabs = byId('tabs');
let pendingEmail = '';     // полный адрес; пуст, когда сервер отдал только маску
let pendingMasked = false; // адрес показан частично — правки и подстановки иначе
let pendingToken = '';     // тикет подтверждения: НЕ сессия, живёт 30 минут
let codeDeadline = 0;
let resendAt = 0;
let ticker = 0;
let stale = false;         // код мёртв (просрочен или сожжены попытки)
let hourLimited = false;   // исчерпан лимит писем на адрес за час

function show(name) {
  Object.keys(states).forEach(function (key) {
    states[key].classList.toggle('is-active', key === name);
  });
  const tabbed = name === 'login' || name === 'register';
  tabs.hidden = !tabbed;
  qsa('.tab').forEach(function (t) { t.classList.toggle('is-on', t.getAttribute('data-go') === name); });
  qsa('.msg').forEach(function (m) { m.className = m.className.replace(' is-shown', ''); });
  if (name !== 'verify' && ticker) { clearInterval(ticker); ticker = 0; }
}

function say(id, text, kind) {
  const box = byId(id);
  box.textContent = text;
  box.className = 'msg msg--' + (kind || 'error') + ' is-shown';
}

function clearMsg(id) { byId(id).className = 'msg msg--error'; }

function busy(button, on, label) {
  button.disabled = on;
  button.textContent = on ? label : button.getAttribute('data-label');
}

// Разбор ошибки в человеческую фразу. 401 приходит без тела, у остальных
// есть ApiError { message, code }.
function reason(err, fallback) {
  if (err.isNetwork) return 'Сеть недоступна. Проверьте соединение и попробуйте ещё раз.';
  if (err.isRateLimit) return err.message;
  return err.message || fallback;
}

// ── Показать / скрыть пароль ───────────────────────────────────────────────
qsa('.toggle').forEach(function (btn) {
  btn.addEventListener('click', function () {
    const input = byId(btn.getAttribute('data-target'));
    const hidden = input.type === 'password';
    input.type = hidden ? 'text' : 'password';
    btn.textContent = hidden ? 'СКРЫТЬ' : 'ПОКАЗАТЬ';
    btn.setAttribute('aria-label', hidden ? 'Скрыть пароль' : 'Показать пароль');
    input.focus();
  });
});

qsa('[data-go]').forEach(function (el) {
  el.addEventListener('click', function (e) {
    if (el.tagName === 'A') e.preventDefault();
    show(el.getAttribute('data-go'));
  });
});

// ── Вход ──────────────────────────────────────────────────────────────────
const loginForm = byId('login-form');
const loginSubmit = byId('login-submit');

loginForm.addEventListener('submit', async function (e) {
  e.preventDefault();
  clearMsg('login-msg');
  const username = byId('login-username').value.trim();
  const password = byId('login-password').value;
  if (!username || !password) { say('login-msg', 'Заполните имя пользователя и пароль.'); return; }

  busy(loginSubmit, true, 'Входим…');
  try {
    saveSession(await login(username, password));
    location.replace(AFTER_LOGIN);
  } catch (err) {
    // Аккаунт есть, но адрес не подтверждён. Раньше здесь сразу дёргали resend —
    // теперь это гарантированный 429: код только что ушёл при регистрации, а на
    // сервере стоит кулдаун. Сервер сам отдаёт всё нужное: тикет, маску адреса и
    // остаток кулдауна, так что письмо заказывает человек, а не страница.
    if (err.status === 403 && err.code === 'email_unverified') {
      const info = err.body || {};
      pendingToken = info.pendingToken || '';
      // Вход по e-mail — адрес знаем целиком; по имени пользователя сервер
      // отдаёт только маску, и полный адрес клиенту взять негде.
      pendingEmail = username.indexOf('@') > 0 ? username : '';
      goVerify({
        email: pendingEmail || info.emailMasked || '',
        masked: !pendingEmail,
        codeExpiresInSeconds: 0,
        resendAvailableInSeconds: info.resendAvailableInSeconds
      });
      busy(loginSubmit, false);
      return;
    }
    if (err.status === 401) say('login-msg', 'Неверное имя пользователя или пароль.');
    else say('login-msg', reason(err, 'Не удалось войти. Попробуйте ещё раз.'));
    busy(loginSubmit, false);
  }
});

// ── Регистрация ───────────────────────────────────────────────────────────
const regForm = byId('register-form');
const regSubmit = byId('register-submit');

regForm.addEventListener('submit', async function (e) {
  e.preventDefault();
  clearMsg('register-msg');
  const username = byId('reg-username').value.trim();
  const email = byId('reg-email').value.trim();
  const password = byId('reg-password').value;

  if (!username) { say('register-msg', 'Придумайте имя пользователя.'); return; }
  if (!email || email.indexOf('@') < 1) { say('register-msg', 'Укажите настоящий e-mail — на него придёт код.'); return; }
  if (password.length < MIN_LENGTH) { say('register-msg', 'Пароль должен быть не короче ' + MIN_LENGTH + ' символов.'); return; }

  busy(regSubmit, true, 'Отправляем код…');
  try {
    const r = await register(username, email, password);
    // Тикет с регистрации — то, что делает «Не мой адрес» доступным сразу.
    // Опечатку замечают именно здесь, через секунду после того, как её сделали.
    pendingToken = (r && r.pendingToken) || '';
    pendingEmail = (r && r.email) || email;
    goVerify({ email: pendingEmail, masked: false, codeExpiresInSeconds: r && r.codeExpiresInSeconds,
               resendAvailableInSeconds: r && r.resendAvailableInSeconds });
  } catch (err) {
    if (err.status === 409) say('register-msg', err.message || 'Это имя или e-mail уже заняты.');
    else say('register-msg', reason(err, 'Не удалось зарегистрироваться. Попробуйте ещё раз.'));
  } finally {
    busy(regSubmit, false);
  }
});

// ── Подтверждение e-mail ──────────────────────────────────────────────────
const codeForm = byId('verify-form');
const codeInput = byId('code');
const codeSubmit = byId('verify-submit');
const resendBtn = byId('resend');
const addrOpen = byId('addr-open');
const addrEdit = byId('addr-edit');
const addrCancel = byId('addr-cancel');
const addrSave = byId('addr-save');
const newEmail = byId('new-email');

// Блоки-состояния из разметки. Видим не больше одного: тексты уже написаны,
// JS только снимает hidden и подставляет числа в [data-slot].
const NOTICES = ['n-wrong', 'n-attempts-out', 'n-expired', 'n-too-soon', 'n-hour-limit',
                 'n-sent', 'n-addr-changed', 'n-email-taken', 'n-email-same'];

function notice(id, slots) {
  NOTICES.forEach(function (key) { byId(key).hidden = key !== id; });
  if (!id) return;
  const box = byId(id);
  Object.keys(slots || {}).forEach(function (name) {
    const slot = box.querySelector('[data-slot="' + name + '"]');
    if (slot) slot.textContent = slots[name];
  });
}

// Адрес: полный или маскированный. Звёздочки сервера заворачиваем в
// .addr__mask, чтобы видимые буквы остались яркими — по ним и узнают чужой
// домен. Собираем узлами, а не innerHTML: значение пришло с сервера.
function renderAddress(value, masked) {
  const box = byId('verify-email');
  box.textContent = '';
  String(value || '').split(/(\*+)/).forEach(function (part) {
    if (!part) return;
    if (part.charAt(0) === '*') {
      const stars = document.createElement('span');
      stars.className = 'addr__mask';
      stars.textContent = part;
      box.appendChild(stars);
    } else {
      box.appendChild(document.createTextNode(part));
    }
  });
  byId('addr-masked-note').hidden = !masked;
  // Исправить адрес можно только по тикету — без него сервер не знает, кто просит.
  addrOpen.hidden = !pendingToken;
}

function mmss(ms) {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
}

// Час — это «в 14:35», а не «через 3600 с»: временем пользоваться удобнее,
// чем обратным отсчётом, который никто не будет досматривать.
function clockIn(seconds) {
  const at = new Date(Date.now() + Math.max(0, seconds) * 1000);
  return String(at.getHours()).padStart(2, '0') + ':' + String(at.getMinutes()).padStart(2, '0');
}

// Код больше не примут (просрочен или сожжены пять попыток): поле гаснет,
// а главная кнопка начинает заказывать новый код — оставлять «Подтвердить»,
// которая заведомо не сработает, хуже.
function setStale(on) {
  stale = on;
  codeForm.classList.toggle('is-stale', on);
  codeInput.readOnly = on;
  codeSubmit.setAttribute('data-label', on ? 'Прислать новый код' : 'Подтвердить');
  tick();
}

function tick() {
  const left = codeDeadline - Date.now();
  byId('code-expiry').textContent = codeDeadline
    ? (left > 0 ? 'Код действует ещё ' + mmss(left) : 'Срок действия кода истёк — запросите новый.')
    : '';

  // Кода больше нет смысла ждать — состояние такое же, как у сожжённых попыток.
  if (codeDeadline && left <= 0 && !stale) {
    setStale(true);
    notice('n-expired');
    return;
  }

  const wait = resendAt - Date.now();
  const waiting = wait > 0;
  const seconds = Math.ceil(wait / 1000);

  // Часовой лимит убирает саму возможность заказать письмо — кнопки нет,
  // время следующей попытки написано в блоке.
  resendBtn.hidden = hourLimited;
  resendBtn.disabled = waiting;
  resendBtn.textContent = waiting ? 'Отправить заново через ' + seconds + ' с' : 'Отправить код заново';

  // Подменённая кнопка подчиняется тому же кулдауну, что и обычная: иначе
  // единственное доступное действие оказывалось бы недоступным.
  if (stale) {
    codeSubmit.hidden = hourLimited;
    codeSubmit.disabled = waiting;
    if (!codeSubmit.disabled) codeSubmit.textContent = 'Прислать новый код';
    else codeSubmit.textContent = 'Новый код через ' + seconds + ' с';
  } else {
    codeSubmit.hidden = false;
    codeSubmit.disabled = false;
  }
}

function startTimers(codeExpiresInSeconds, resendInSeconds) {
  const life = Number(codeExpiresInSeconds) || 0;
  if (life > 0) codeDeadline = Date.now() + life * 1000;

  // Сервер знает, сколько осталось на самом деле: войти могли через двадцать
  // секунд после регистрации, и тогда ждать не минуту, а сорок секунд.
  const wait = resendInSeconds == null ? RESEND_COOLDOWN : Number(resendInSeconds) || 0;
  resendAt = Date.now() + wait * 1000;

  if (!ticker) ticker = setInterval(tick, 1000);
  tick();
}

function goVerify(info) {
  if (!pendingToken && !pendingEmail) {
    show('login');
    say('login-msg', 'E-mail не подтверждён. Войдите по адресу почты, чтобы получить код.');
    return;
  }
  pendingMasked = !!info.masked;
  renderAddress(info.email, pendingMasked);
  hourLimited = false;
  codeDeadline = 0;
  setStale(false);
  notice(null);
  closeAddrEdit();
  show('verify');
  startTimers(info.codeExpiresInSeconds, info.resendAvailableInSeconds);
  codeInput.value = '';
  codeInput.focus();
}

// Тикет живёт 30 минут. Дальше сервер не знает, кто перед ним, и единственный
// честный ответ — войти заново; это единственное, что меняет экран целиком.
function sessionExpired() {
  pendingToken = '';
  notice(null);
  show('verifyExpired');
}

// Письмо заказано откуда угодно: ссылка, кнопка в блоке, подменённая главная.
async function requestCode() {
  clearMsg('verify-msg');
  resendBtn.disabled = true;
  codeSubmit.disabled = true;
  try {
    const r = await resendCode(pendingToken ? '' : pendingEmail, pendingToken);
    hourLimited = false;
    setStale(false);
    codeDeadline = 0;
    startTimers(r && r.codeExpiresInSeconds, r && r.resendAvailableInSeconds);
    notice('n-sent');
  } catch (err) {
    handleSendError(err);
  }
}

function handleSendError(err) {
  if (err.status === 400 && err.code === 'invalid_ticket') { sessionExpired(); return; }

  if (err.isRateLimit) {
    const after = err.retryAfter || 0;
    if (err.code === 'email_rate_limited') {
      // Не полный тупик: последний код ещё жив, поле остаётся рабочим.
      hourLimited = true;
      notice('n-hour-limit', { at: clockIn(after || 3600) });
    } else {
      resendAt = Date.now() + (after || RESEND_COOLDOWN) * 1000;
      notice('n-too-soon', { seconds: Math.max(1, after || RESEND_COOLDOWN) + ' с' });
    }
    tick();
    return;
  }

  say('verify-msg', reason(err, 'Не удалось отправить код. Попробуйте позже.'));
  tick();
}

codeInput.addEventListener('input', function () {
  const digits = codeInput.value.replace(/\D+/g, '').slice(0, 6);
  if (digits !== codeInput.value) codeInput.value = digits;
  if (digits.length === 6 && !stale) codeForm.requestSubmit();
});

codeForm.addEventListener('submit', async function (e) {
  e.preventDefault();

  // В stale-режиме кнопка означает другое действие, и поле проверять незачем.
  if (stale) { requestCode(); return; }

  clearMsg('verify-msg');
  const code = codeInput.value.trim();
  if (code.length !== 6) { say('verify-msg', 'Код состоит из шести цифр.'); return; }

  busy(codeSubmit, true, 'Проверяем…');
  try {
    saveSession(await verifyEmail(pendingEmail, code, pendingToken));
    location.replace(AFTER_LOGIN);
  } catch (err) {
    busy(codeSubmit, false);

    // 409 already_verified — код уже не нужен, отправляем на вход.
    if (err.status === 409) {
      notice(null);
      say('verify-msg', 'Адрес уже подтверждён — войдите обычным способом.', 'ok');
      setTimeout(function () { show('login'); }, 1200);
      return;
    }
    if (err.status === 400 && err.code === 'invalid_ticket') { sessionExpired(); return; }

    // Пять неудач подряд и просроченный код приводят в одно и то же место:
    // этот код мёртв, помочь может только новый.
    if (err.code === 'too_many_attempts') { setStale(true); notice('n-attempts-out'); return; }
    if (err.code === 'code_expired')      { setStale(true); notice('n-expired');      return; }

    if (err.code === 'invalid_code') {
      const left = err.body && typeof err.body.attemptsLeft === 'number' ? err.body.attemptsLeft : null;
      if (left === 0) { setStale(true); notice('n-attempts-out'); return; }
      notice('n-wrong', left == null ? {} : { attempts: left });
      codeInput.select();
      return;
    }

    say('verify-msg', reason(err, 'Не удалось подтвердить адрес. Попробуйте ещё раз.'));
    codeInput.select();
  }
});

resendBtn.addEventListener('click', requestCode);

// Кнопка «Прислать новый код» внутри блоков состояний.
qsa('[data-act="resend"]').forEach(function (btn) {
  btn.addEventListener('click', requestCode);
});

// ── Смена адреса ──────────────────────────────────────────────────────────
// Раскрывающийся блок, а не отдельный экран: поле кода остаётся на месте,
// и возврат ничего не стоит.
function openAddrEdit() {
  addrEdit.hidden = false;
  addrOpen.setAttribute('aria-expanded', 'true');
  newEmail.value = pendingMasked ? '' : pendingEmail;
  newEmail.focus();
}

function closeAddrEdit() {
  addrEdit.hidden = true;
  addrOpen.setAttribute('aria-expanded', 'false');
}

addrOpen.addEventListener('click', function () {
  if (addrEdit.hidden) openAddrEdit(); else closeAddrEdit();
});

addrCancel.addEventListener('click', function () {
  notice(null);
  closeAddrEdit();
  codeInput.focus();
});

addrSave.addEventListener('click', async function () {
  clearMsg('verify-msg');
  const value = newEmail.value.trim();
  if (!value || value.indexOf('@') < 1) { newEmail.reportValidity(); newEmail.focus(); return; }
  if (!pendingToken) { sessionExpired(); return; }

  busy(addrSave, true, 'Отправляем…');
  try {
    const r = await changeEmail(pendingToken, value);
    // Адрес теперь наш и известен целиком — маска больше не нужна.
    pendingEmail = (r && r.email) || value;
    pendingMasked = false;
    hourLimited = false;   // лимит считался по старому адресу
    renderAddress(pendingEmail, false);
    closeAddrEdit();
    setStale(false);
    codeDeadline = 0;
    startTimers(r && r.codeExpiresInSeconds, r && r.resendAvailableInSeconds);
    notice('n-addr-changed', { email: pendingEmail });
    codeInput.value = '';
    codeInput.focus();
  } catch (err) {
    if (err.status === 409 && err.code === 'email_taken') { notice('n-email-taken'); return; }
    if (err.status === 400 && err.code === 'email_unchanged') { notice('n-email-same'); return; }
    if (err.status === 400 && err.code === 'invalid_ticket') { sessionExpired(); return; }
    if (err.isRateLimit) { closeAddrEdit(); handleSendError(err); return; }
    say('verify-msg', reason(err, 'Не удалось сменить адрес. Попробуйте ещё раз.'));
  } finally {
    busy(addrSave, false);
  }
});

// ── Забыли пароль ─────────────────────────────────────────────────────────
const forgotForm = byId('forgot-form');
const forgotSubmit = byId('forgot-submit');

forgotForm.addEventListener('submit', async function (e) {
  e.preventDefault();
  clearMsg('forgot-msg');
  const email = byId('forgot-email').value.trim();
  if (!email || email.indexOf('@') < 1) { say('forgot-msg', 'Укажите e-mail, на который оформлен аккаунт.'); return; }

  busy(forgotSubmit, true, 'Отправляем…');
  try {
    // Сервер отвечает 202 всегда — есть такой адрес или нет, мы не узнаём.
    await requestPasswordReset(email);
    byId('sent-email').textContent = email;
    show('sent');
  } catch (err) {
    say('forgot-msg', reason(err, 'Не удалось отправить письмо. Попробуйте позже.'));
  } finally {
    busy(forgotSubmit, false);
  }
});

// ── Стартовый экран: ?mode=register открывает регистрацию ─────────────────
show(queryParam('mode') === 'register' ? 'register' : 'login');
