// Один в один по контракту HorusAPI v1. Ничего кроме путей, тел и типов
// ответов здесь нет — разбор ошибок в api.js, интерпретация в страницах.

import { get, post, del, put } from './api.js';

// ── Auth ──────────────────────────────────────────────────────────────────
// 200 LoginResponse { session, expiresAt } · 400/403 ApiError · 401 без тела
// Поле username принимает и имя пользователя, и e-mail (single-field login).
export const login = (username, password) =>
  post('/auth/login', { username, password }, { auth: false });

// 202 RegisterResponse { status, email, codeExpiresInSeconds } · 400/409/429
export const register = (username, email, password) =>
  post('/auth/register', { username, password, email }, { auth: false });

// 200 LoginResponse · 400/409/429
export const verifyEmail = (email, code) =>
  post('/auth/verify', { email, code }, { auth: false });

// 202 RegisterResponse · 400/429
export const resendCode = (email) =>
  post('/auth/resend-code', { email }, { auth: false });

// 202 StatusResponse — всегда, чтобы не выдавать, есть ли такой адрес
export const requestPasswordReset = (email) =>
  post('/auth/reset-request', { email }, { auth: false });

// 200 StatusResponse { status: 'valid' | ... }
export const checkResetToken = (token) =>
  get('/auth/reset-check?token=' + encodeURIComponent(token), { auth: false });

// 200 StatusResponse · 400 ApiError (code: 'invalid_token')
export const confirmPasswordReset = (token, password) =>
  post('/auth/reset-confirm', { token, password }, { auth: false });

// 204 · требует X-Session-Key
export const logoutOthers = () => post('/auth/logout-others', {});

// ── Account ───────────────────────────────────────────────────────────────
// 200 WhoAmIResponse { ip, ipVersion, username, email, emailVerified,
//                      subscriptionExpiresAt, currentServerId, observedAt }
export const whoAmI = () => get('/whoami');

// ── Servers ───────────────────────────────────────────────────────────────
// 200 PingCandidate[] { id, country, city, host, current_load, reserved_count, max_clients }
// Наименее загруженные узлы со свободными местами, по одному на страну — для TCP-пинга.
export const serverCandidates = () => get('/servers');
export const bestServers = serverCandidates;   // старое имя, чтобы не трогать импорт cabinet.js

// Зарезервировать/сменить узел. Пусто или { server_id: null } → автоподбор.
// 200 BoundServer { id, name, country, city, host } · 403/404/409 ApiError
export const selectServer = (serverId) => post('/servers/select', { server_id: serverId ?? null });

// GET /servers/connect с заголовком X-Session-Key (наше приложение/сайт) →
// 200 { server:{id,name,country,city,host}, vless:[…], hysteria2, olcrtc:{…}|null }
// 403 subscription_expired · 409 no_capacity. (Сторонние клиенты дергают тот же
// путь c ?key=… и получают base64-подписку text/plain — это не для сайта.)
export const connectConfig = () => get('/servers/connect');

// ── Health ────────────────────────────────────────────────────────────────
export const health = () => get('/health', { auth: false });

export { del, put };
