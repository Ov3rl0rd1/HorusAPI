// Админские вызовы HorusAPI, один в один по контракту /admin/*. Транспорт,
// заголовок сессии и разбор ошибок — общие с сайтом, из /js/api.js.

import { get, post, put, del } from '/js/api.js';

const enc = encodeURIComponent;

export const whoAmI = () => get('/whoami');

// ── Ноды ─────────────────────────────────────────────────────────────────
// ServerAdminItem[] { id, name, country, city, host, current_load, max_clients,
//   max_reservations, reserved_count, is_active, masquerade_url, profile,
//   agent_version, last_registered_at, auth_password }
export const servers        = () => get('/admin/servers');
// ServerProfileState[] { id, profile, desired_profile, assigned_profile, in_sync,
//   render_error, warnings[], offer_count, … }
export const serverProfiles = () => get('/admin/servers/profiles');
// FleetProfile { default_profile, nodes_total, nodes_overridden }
export const fleetProfile   = () => get('/admin/fleet/profile');

export const setFleetProfile  = (profile)     => put('/admin/fleet/profile', { profile });
export const setServerProfile = (id, profile) => put(`/admin/servers/${id}/profile`, { profile });

// 201 { id } · 400
export const addServer    = (body) => post('/admin/servers', body);
export const removeServer = (id)   => del(`/admin/servers/${id}`);

// PingResult[] { id, name, reachable, statusCode, error }
export const pingServers = () => post('/admin/servers/ping', {});

// ServerDetail { node: ServerNodeInfo { …, bound_users, pending_holds, offers_json, … },
//   offers: OfferSummary[] { id, label, tag, protocol, audience[], has_uri },
//   users: NodeUserItem[] { id, username, email, is_admin, expires_at, last_disconnect_at, … } }
export const server = (id) => get(`/admin/servers/${id}`);

// EvacuationReport { serverId, total, moved, stayed, failed, problems[] }
export const evacuate = (id) => post(`/admin/servers/${id}/evacuate`, {});
export const activate = (id) => post(`/admin/servers/${id}/activate`, {});

// Один пользователь с ноды; to — id ноды, null — наименее загруженная другая.
// 200 UserMoveReport { user_id, username, from_server_id, server_id, server_name, problems[] }
// 400 same_server · 404 user_not_found/target_not_found · 409 not_on_server/target_inactive/no_capacity
export const moveUser = (serverId, userId, to) =>
  post(`/admin/servers/${serverId}/users/${userId}/evacuate`, { server_id: to == null ? null : to });

// ── Пользователи ─────────────────────────────────────────────────────────
// UserAdminItem[] { id, username, email, email_verified, is_admin, is_active,
//   created_at, expires_at, current_server_id, server_name }
export const users = (q) => get('/admin/users?q=' + enc(q || ''));
// TrafficMonthItem[] { month, total_bytes, olcrtc_bytes, server_name, updated_at } — до 6 месяцев, новые первыми
export const userTraffic = (username) => get(`/admin/users/${enc(username)}/traffic`);

// 204 · 404 · 409 no_capacity
export const grantComp  = (username, expiresAt) =>
  put(`/admin/users/${enc(username)}/subscription`, { expires_at: expiresAt });
export const revokeComp = (username) => del(`/admin/users/${enc(username)}/subscription`);

// Открыть непубличный тариф («для своих») — купить его пользователь сможет сам.
// expiresAt null — бессрочно. Повторная выдача заменяет срок.
export const grantPlan = (username, planCode, expiresAt) =>
  post(`/admin/users/${enc(username)}/grant`, { plan_code: planCode, expires_at: expiresAt });

// PlanGrantItem[] { user_id, username, email, plan_id, plan_code, plan_title,
//   plan_is_public, expires_at (null — бессрочно), created_at, granted_by }
export const userGrants  = (username) => get(`/admin/users/${enc(username)}/grants`);
export const revokeGrant = (username, planCode) =>
  del(`/admin/users/${enc(username)}/grants/${enc(planCode)}`);

// ── Тарифы ───────────────────────────────────────────────────────────────
// PlanAdminItem[] { id, code, title, tier, kind, interval_unit, interval_count, amount,
//   currency, is_public, is_active, created_at, live_subscriptions, grants }
export const plans = () => get('/admin/plans');
// body { code, title, tier, kind, interval_unit, interval_count, amount, is_public, is_active }
// 201 { id, code } · 400 invalid_plan · 409 plan_exists
export const createPlan = (body) => post('/admin/plans', body);
// code не меняется. 200 PlanAdminItem · 409 plan_in_use (тип/период при живых подписках)
export const updatePlan = (id, body) => put(`/admin/plans/${id}`, body);
export const planGrants = (id) => get(`/admin/plans/${id}/grants`);

// ── Платежи ──────────────────────────────────────────────────────────────
// PaymentAdminItem[] { id, user_id, username, plan_code, kind, amount, discount,
//   currency, status, provider, provider_ref, created_at, … } — последние 500
export const payments = (username) =>
  get('/admin/payments' + (username ? '?user=' + enc(username) : ''));

// 200 { status: 'refunded' | 'manual_required', detail } · 400 not_refundable
export const refund = (id, amount, reason) =>
  post(`/admin/payments/${id}/refund`, { amount, reason });

// ── Промокоды ────────────────────────────────────────────────────────────
// PromoRow[] { id, code, kind, percent_off, max_redemptions, redeemed_count,
//   per_user_limit, plan_id, starts_at, ends_at, is_active }
export const promos          = () => get('/admin/promocodes');
export const createPromo     = (body) => post('/admin/promocodes', body);
export const deactivatePromo = (code) => del(`/admin/promocodes/${enc(code)}`);

// ── Партнёры (реферальная программа) ─────────────────────────────────────
// ReferralPartnerAdminItem[] { user_id, username, email, code, discount_percent, reward_percent,
//   is_active, note, created_at, invited, paying, revenue, earned, paid_out, balance } — рубли целые
export const referrals = () => get('/admin/referrals');
// ReferralPartnerDetail { partner, link, invited[], rewards[], payouts[] } · 404 not_partner
export const referral  = (username) => get(`/admin/referrals/${enc(username)}`);
// Создать: { code, discount_percent, reward_percent, is_active?, note? }. Изменить — только то,
// что меняется ({ is_active: false } — выключить). 200 ReferralPartnerAdminItem ·
// 400 invalid_referral · 404 user_not_found · 409 code_taken
export const saveReferral = (username, body) => put(`/admin/users/${enc(username)}/referral`, body);
// 200 ReferralPartnerAdminItem · 404 not_partner · 409 payout_exceeds_balance
export const referralPayout = (username, amount, note) =>
  post(`/admin/users/${enc(username)}/referral/payouts`, { amount, note });
