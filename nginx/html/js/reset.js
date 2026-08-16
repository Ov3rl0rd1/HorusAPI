import { checkResetToken, confirmPasswordReset } from './endpoints.js';
import { byId, qsa, queryParam } from './util.js';

const DEFAULT_INVALID = 'Ссылка для сброса пароля истекла или уже была использована. Запросите новую в приложении.';
const MIN_LENGTH = 8;

const token = queryParam('token');

const states = {
  loading: byId('state-loading'),
  form: byId('state-form'),
  done: byId('state-done'),
  invalid: byId('state-invalid'),
};
const form = byId('form');
const pass = byId('password');
const confirm = byId('confirm');
const submit = byId('submit');
const msg = byId('form-msg');

function show(name) {
  Object.keys(states).forEach((key) => states[key].classList.toggle('is-active', key === name));
}

function fail(text) {
  byId('invalid-text').textContent = text || DEFAULT_INVALID;
  show('invalid');
}

function say(text) {
  msg.textContent = text;
  msg.className = 'msg msg--error is-shown';
}

function clearSay() {
  msg.className = 'msg msg--error';
  pass.setAttribute('aria-invalid', 'false');
  confirm.setAttribute('aria-invalid', 'false');
}

// ── Показать / скрыть пароль ───────────────────────────────────────────────
qsa('.toggle').forEach((btn) => {
  btn.addEventListener('click', () => {
    const input = byId(btn.getAttribute('data-target'));
    const hidden = input.type === 'password';
    input.type = hidden ? 'text' : 'password';
    btn.textContent = hidden ? 'СКРЫТЬ' : 'ПОКАЗАТЬ';
    btn.setAttribute('aria-label', hidden ? 'Скрыть пароль' : 'Показать пароль');
    input.focus();
  });
});

// ── Проверяем ссылку до того, как человек начнёт печатать ──────────────────
async function check() {
  if (!token) {
    fail('В ссылке нет кода восстановления. Откройте ссылку из письма целиком.');
    return;
  }
  try {
    const data = await checkResetToken(token);
    if (data && data.status === 'valid') {
      show('form');
      pass.focus();
    } else {
      fail();
    }
  } catch (err) {
    if (err.isRateLimit) fail(err.message);
    else if (err.isNetwork) fail('Не удалось проверить ссылку. Проверьте соединение и попробуйте ещё раз.');
    else fail();
  }
}

// ── Отправка нового пароля ────────────────────────────────────────────────
form.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearSay();

  if (pass.value.length < MIN_LENGTH) {
    pass.setAttribute('aria-invalid', 'true');
    say('Пароль должен быть не короче ' + MIN_LENGTH + ' символов.');
    pass.focus();
    return;
  }
  if (pass.value !== confirm.value) {
    confirm.setAttribute('aria-invalid', 'true');
    say('Пароли не совпадают.');
    confirm.focus();
    return;
  }

  submit.disabled = true;
  submit.textContent = 'Сохраняем…';

  try {
    await confirmPasswordReset(token, pass.value);
    show('done');
  } catch (err) {
    if (err.isRateLimit) say(err.message);
    else if (err.isNetwork) say('Сеть недоступна. Проверьте соединение и попробуйте ещё раз.');
    // ссылку успели использовать или она истекла, пока заполняли форму
    else if (err.code === 'invalid_token') fail();
    else say(err.message || 'Не удалось сохранить пароль. Попробуйте ещё раз.');
  } finally {
    submit.disabled = false;
    submit.textContent = 'Сохранить пароль';
  }
});

check();
