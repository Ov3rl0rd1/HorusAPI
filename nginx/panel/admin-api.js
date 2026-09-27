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

// EvacuationReport { serverId, total, moved, stayed, failed, problems[] }
export const evacuate = (id) => post(`/admin/servers/${id}/evacuate`, {});
export const activate = (id) => post(`/admin/servers/${id}/activate`, {});

// ── Пользователи ─────────────────────────────────────────────────────────
// UserAdminItem[] { id, username, email, email_verified, is_admin, is_active,
//   created_at, expires_at, current_server_id, server_name }
export const users = (q) => get('/admin/users?q=' + enc(q || ''));

// 204 · 404 · 409 no_capacity
export const grantComp  = (username, expiresAt) =>
  put(`/admin/users/${enc(username)}/subscription`, { expires_at: expiresAt });
export const revokeComp = (username) => del(`/admin/users/${enc(username)}/subscription`);

// Открыть непубличный тариф («для своих») — купить его пользователь сможет сам.
export const grantPlan = (username, planCode, expiresAt) =>
  post(`/admin/users/${enc(username)}/grant`, { plan_code: planCode, expires_at: expiresAt });

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
