// Один в один по контракту HorusAPI v1. Ничего кроме путей, тел и типов
// ответов здесь нет — разбор ошибок в api.js, интерпретация в страницах.

import { get, post, del, put, API_BASE } from './api.js';
import { getSessionKey } from './session.js';

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
// 200 PingCandidate[] { id, country, city, host,
//                       current_load, reserved_count, max_clients }
// Наименее загруженные узлы с запасом мест, по одному на страну.
export const serverCandidates = () => get('/servers');

// 200 BoundServer { id, name, country, city, host }
// serverId не передан → сервер подбирается по наименьшей загрузке.
// 403 подписка неактивна · 404 сервер не найден · 409 no_capacity
export const selectServer = (serverId) =>
  post('/servers/select', serverId == null ? null : { server_id: serverId });

// 200 ConnectResponse { server, vless[], hysteria2, olcrtc }
// Сессия в заголовке → JSON. С ?key= тот же путь отдаёт base64-подписку.
export const connectInfo = () => get('/servers/connect');

// Личная ссылка подписки для клиентов: её достаточно вставить в приложение.
export function subscriptionUrl() {
  const key = getSessionKey();
  if (!key) return '';
  const base = API_BASE || location.origin;
  return base.replace(/\/$/, '') + '/servers/connect?key=' + encodeURIComponent(key);
}

// ── Billing ─────────────────────────────────────────────────────────────────
// 200 PlanView[] { code, title, tier, kind, interval_unit, interval_count,
//                  amount, currency, is_public } — планы, доступные этому юзеру.
export const billingPlans = () => get('/billing/plans');

// 200 CheckoutView { redirect, amount, discount, currency, kind }
// 400 promo_invalid/promo_not_applicable · 404 plan_not_found · 409 no_capacity · 502 provider_error
export const billingCheckout = (planCode, promoCode) =>
  post('/billing/checkout', { plan_code: planCode, promo_code: promoCode || null });

// 200 SubscriptionView { status, plan_code, current_period_end, cancel_at_period_end, kind }
// status = 'none', когда подписки нет.
export const billingSubscription = () => get('/billing/subscription');

// 204 · 404 no_subscription · 502 provider_error
export const billingCancel = () => post('/billing/cancel', {});

// ── Health ────────────────────────────────────────────────────────────────
export const health = () => get('/health', { auth: false });

export { del, put };
