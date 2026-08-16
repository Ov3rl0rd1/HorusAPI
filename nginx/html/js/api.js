// Транспорт до HorusAPI. Схема авторизации своя: токен из /auth/login или
// /auth/verify уходит в заголовке X-Session-Key. Тело ошибки — ApiError
// { message, code? }, у 401 тела нет вовсе.
//
// Именованные вызовы контракта — в endpoints.js, хранение сессии — в session.js.

import { getSessionKey } from './session.js';

export const API_BASE = '';

export class ApiError extends Error {
  constructor(message, { status = 0, code = '', retryAfter = 0, body = null } = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;       // 0 — до сервера не дошли
    this.code = code;          // ApiError.code из тела
    this.retryAfter = retryAfter;
    this.body = body;
  }
  get isNetwork()   { return this.status === 0; }
  get isRateLimit() { return this.status === 429; }
  get isAuth()      { return this.status === 401; }
}

// «Слишком много запросов» — покажем, через сколько можно повторить.
export function retryAfterText(seconds) {
  const after = parseInt(seconds, 10);
  if (!after) return 'Слишком много попыток. Попробуйте позже.';
  const minutes = Math.ceil(after / 60);
  return 'Слишком много попыток. Повторите через ' + (after < 60 ? after + ' с.' : minutes + ' мин.');
}

export async function request(path, { method = 'GET', body, headers, signal, auth = true } = {}) {
  const head = { Accept: 'application/json' };
  if (body !== undefined) head['Content-Type'] = 'application/json';
  if (auth) {
    const key = getSessionKey();
    if (key) head['X-Session-Key'] = key;
  }
  Object.assign(head, headers);

  const init = { method, headers: head, signal };
  if (body !== undefined) init.body = JSON.stringify(body);

  let response;
  try {
    response = await fetch(API_BASE + path, init);
  } catch (err) {
    throw new ApiError('Сеть недоступна. Проверьте соединение и попробуйте ещё раз.', { status: 0 });
  }

  if (response.status === 429) {
    const after = response.headers.get('Retry-After');
    throw new ApiError(retryAfterText(after), { status: 429, retryAfter: parseInt(after, 10) || 0 });
  }

  const empty = response.status === 204 || response.status === 401;
  const data = empty ? null : await response.json().catch(() => null);

  if (!response.ok) {
    throw new ApiError(
      (data && (data.message || data.Message)) || '',
      { status: response.status, code: (data && data.code) || '', body: data }
    );
  }
  return data;
}

export const get  = (path, opts)       => request(path, opts);
export const post = (path, body, opts) => request(path, Object.assign({ method: 'POST', body }, opts));
export const put  = (path, body, opts) => request(path, Object.assign({ method: 'PUT',  body }, opts));
export const del  = (path, opts)       => request(path, Object.assign({ method: 'DELETE' }, opts));
