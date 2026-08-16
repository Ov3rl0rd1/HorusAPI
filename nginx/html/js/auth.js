import { login, register, verifyEmail, resendCode, requestPasswordReset } from './endpoints.js';
import { saveSession, isSignedIn } from './session.js';
import { byId, qsa, queryParam } from './util.js';

const AFTER_LOGIN = 'cabinet.html';   // личный кабинет
const MIN_LENGTH = 8;
const RESEND_COOLDOWN = 60;           // секунд до повторной отправки кода

if (isSignedIn()) location.replace(AFTER_LOGIN);

const states = {
  login: byId('state-login'),
  register: byId('state-register'),
  verify: byId('state-verify'),
  forgot: byId('state-forgot'),
  sent: byId('state-sent')
};
const tabs = byId('tabs');
let pendingEmail = '';
let codeDeadline = 0;
let resendAt = 0;
let ticker = 0;

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
    // Аккаунт есть, но адрес не подтверждён — уводим на экран с кодом.
    if (err.status === 403 && err.code === 'email_unverified') {
      pendingEmail = username.indexOf('@') > 0 ? username : '';
      if (pendingEmail) {
        busy(loginSubmit, false);
        try {
          const r = await resendCode(pendingEmail);
          goVerify(r, pendingEmail);
        } catch (e2) {
          goVerify(null, pendingEmail);
          say('verify-msg', reason(e2, 'Не удалось отправить код. Попробуйте позже.'));
        }
        return;
      }
      say('login-msg', 'E-mail не подтверждён. Войдите по адресу почты, чтобы получить новый код.');
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
    goVerify(r, email);
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

function goVerify(response, fallbackEmail) {
  pendingEmail = (response && response.email) || fallbackEmail;
  byId('verify-email').textContent = pendingEmail;
  show('verify');
  startTimers(response && response.codeExpiresInSeconds);
  codeInput.value = '';
  codeInput.focus();
}

function mmss(ms) {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0');
}

function tick() {
  const left = codeDeadline - Date.now();
  byId('code-expiry').textContent = left > 0
    ? 'Код действует ещё ' + mmss(left)
    : 'Срок действия кода истёк — запросите новый.';
  const wait = resendAt - Date.now();
  resendBtn.disabled = wait > 0;
  resendBtn.textContent = wait > 0
    ? 'Отправить заново через ' + Math.ceil(wait / 1000) + ' с'
    : 'Отправить код заново';
}

function startTimers(codeExpiresInSeconds) {
  codeDeadline = Date.now() + (Number(codeExpiresInSeconds) || 0) * 1000;
  resendAt = Date.now() + RESEND_COOLDOWN * 1000;
  if (!ticker) ticker = setInterval(tick, 1000);
  tick();
}

codeInput.addEventListener('input', function () {
  const digits = codeInput.value.replace(/\D+/g, '').slice(0, 6);
  if (digits !== codeInput.value) codeInput.value = digits;
  if (digits.length === 6) codeForm.requestSubmit();
});

codeForm.addEventListener('submit', async function (e) {
  e.preventDefault();
  clearMsg('verify-msg');
  const code = codeInput.value.trim();
  if (code.length !== 6) { say('verify-msg', 'Код состоит из шести цифр.'); return; }

  busy(codeSubmit, true, 'Проверяем…');
  try {
    saveSession(await verifyEmail(pendingEmail, code));
    location.replace(AFTER_LOGIN);
  } catch (err) {
    // 409 already_verified — код уже не нужен, отправляем на вход.
    if (err.status === 409) {
      say('verify-msg', 'Адрес уже подтверждён — войдите обычным способом.', 'ok');
      setTimeout(function () { show('login'); }, 1200);
      busy(codeSubmit, false);
      return;
    }
    if (err.status === 400) say('verify-msg', err.message || 'Код неверный или устарел.');
    else say('verify-msg', reason(err, 'Не удалось подтвердить адрес. Попробуйте ещё раз.'));
    busy(codeSubmit, false);
    codeInput.select();
  }
});

resendBtn.addEventListener('click', async function () {
  clearMsg('verify-msg');
  resendBtn.disabled = true;
  try {
    const r = await resendCode(pendingEmail);
    startTimers(r && r.codeExpiresInSeconds);
    say('verify-msg', 'Новый код отправлен на ' + pendingEmail, 'ok');
  } catch (err) {
    say('verify-msg', reason(err, 'Не удалось отправить код. Попробуйте позже.'));
    resendBtn.disabled = false;
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
