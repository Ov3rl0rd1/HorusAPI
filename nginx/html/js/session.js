// Сессия живёт в localStorage: { session, expiresAt }. Ключ уходит в
// X-Session-Key автоматически (см. api.js).

const STORE_KEY = 'horus.session';

// Копия ключа в cookie — только ради админки на /panel. Переход по адресу в
// браузере не умеет слать заголовок X-Session-Key, а nginx пускает на /panel,
// лишь когда API по этому ключу подтвердит, что вход админский (auth_request →
// /admin/gate). Всем остальным там обычный 404.
//
// Path=/panel — cookie не уходит ни на один другой адрес, в том числе на API.
// SameSite=Lax, а не Strict: со Strict переход на /panel по ссылке из Telegram
// пришёл бы без cookie и выглядел как 404. Под /panel одна статика, а всё, что
// что-то меняет, идёт в API с заголовком, который чужой сайт подделать не может.
//
// Значение пишется как есть: ключ — «{id}.{base64}», а все символы base64
// допустимы в cookie без кодирования. Закодированное nginx передал бы в API
// буквально, и ключ бы не совпал.
const PANEL_COOKIE = 'horus_panel';

function panelCookie(value, expires) {
  try {
    let c = PANEL_COOKIE + '=' + value + '; Path=/panel; SameSite=Lax';
    if (expires) c += '; Expires=' + expires.toUTCString();
    if (location.protocol === 'https:') c += '; Secure';
    document.cookie = c;
  } catch (e) {}
}

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
  syncPanelCookie();
}

export function clearSession() {
  try { localStorage.removeItem(STORE_KEY); } catch (e) {}
  syncPanelCookie();
}

// Cookie с Path=/panel страница с другого адреса прочитать не может, поэтому
// сверять нечего — она просто переписывается по localStorage при каждой
// загрузке. Так её получает и админ, вошедший до появления панели, и
// выход в соседней вкладке её снимает.
function syncPanelCookie() {
  const s = readSession();
  if (s) panelCookie(s.session, s.expiresAt ? new Date(s.expiresAt) : null);
  else panelCookie('', new Date(0));
}

syncPanelCookie();

export function isSignedIn() { return !!getSessionKey(); }

// Для страниц под замком: нет сессии — уводим на вход.
export function requireSession(loginUrl) {
  if (isSignedIn()) return true;
  location.replace(loginUrl || '/login');
  return false;
}
