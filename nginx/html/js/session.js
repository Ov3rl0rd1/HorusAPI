// Сессия живёт в localStorage: { session, expiresAt }. Ключ уходит в
// X-Session-Key автоматически (см. api.js).

const STORE_KEY = 'horus.session';

export function readSession() {
  try {
    const raw = localStorage.getItem(STORE_KEY);
    if (!raw) return null;
    const data = JSON.parse(raw);
    if (!data || !data.session) return null;
    if (data.expiresAt && new Date(data.expiresAt).getTime() <= Date.now()) {
      localStorage.removeItem(STORE_KEY);
      return null;
    }
    return data;
  } catch (e) { return null; }
}

export function getSessionKey() {
  const s = readSession();
  return s ? s.session : '';
}

// LoginResponse — { session, expiresAt }
export function saveSession(loginResponse) {
  try {
    localStorage.setItem(STORE_KEY, JSON.stringify({
      session: loginResponse.session,
      expiresAt: loginResponse.expiresAt || null
    }));
  } catch (e) {}
}

export function clearSession() {
  try { localStorage.removeItem(STORE_KEY); } catch (e) {}
}

export function isSignedIn() { return !!getSessionKey(); }

// Для страниц под замком: нет сессии — уводим на вход.
export function requireSession(loginUrl) {
  if (isSignedIn()) return true;
  location.replace(loginUrl || 'login.html');
  return false;
}
