namespace HorusAPI.Models;

/// <summary><c>username</c> accepts either a username or an e-mail address (single-field login).</summary>
public record LoginRequest(string username, string password);
public record LoginResponse(string session, DateTime? expiresAt);
public record RegisterRequest(string username, string password, string email);
public record LogoutOthersRequest();

// ── E-mail confirmation ──────────────────────────────────────────────────────

/// <summary>
/// Identify the account by <c>email</c> or by <c>pending_token</c> — the ticket /auth/login
/// hands back for an unconfirmed account. The ticket path exists because someone who signed
/// in with their USERNAME has never told the client which address to quote, and because the
/// address it would have to quote is one we only ever show masked.
/// </summary>
public record VerifyRequest(string email, string code, string? pending_token = null);

/// <summary>Same two ways in as <see cref="VerifyRequest"/>; at least one is required.</summary>
public record ResendCodeRequest(string? email, string? pending_token = null);

/// <summary>Correct a mistyped address on an account that has not been confirmed yet.</summary>
public record ChangeEmailRequest(string pending_token, string email);

/// <summary>
/// A wrong code, with how many guesses are left before it dies. The count comes from the
/// server because the client cannot keep it: a page reload would restart it, and a resend
/// resets it to five.
/// </summary>
public record VerifyCodeError(string Message, string? Code, int attemptsLeft);

/// <summary>
/// Answer to /auth/register and to a resend: the account exists but cannot log in until the
/// code is entered. <c>resendAvailableInSeconds</c> is what the "send again" button counts
/// down from.
/// </summary>
/// <param name="pendingToken">
/// Set ONLY by /auth/register, where the caller just created the account and is therefore
/// its owner. /auth/resend-code must never fill it in: that endpoint is anonymous and
/// answers for any address, so handing out a ticket there would let anyone claim any
/// account by naming its address.
/// </param>
public record RegisterResponse(string status, string email, int codeExpiresInSeconds,
    int resendAvailableInSeconds = 0, string? pendingToken = null);

/// <summary>
/// Answer to logging in to an account whose address is not confirmed. Carries
/// <c>code = "email_unverified"</c> so clients that only look at the code keep working, plus
/// everything the confirmation screen needs: a ticket, the masked address to show, and the
/// two countdowns.
///
/// The ticket is NOT a session. SessionAuthHandler reads users.sessions[] and never looks at
/// pending_logins, so it opens the confirmation flow and nothing else.
/// </summary>
public record PendingVerificationResponse(
    string Message,
    string Code,
    string emailMasked,
    string pendingToken,
    int    pendingExpiresInSeconds,
    int    resendAvailableInSeconds,
    int    codeExpiresInSeconds);

// ── Password reset ───────────────────────────────────────────────────────────
public record ResetRequest(string email);
public record ResetConfirmRequest(string token, string password);

/// <summary>Deliberately vague: /auth/reset-request answers the same way for unknown addresses.</summary>
public record StatusResponse(string status);

// ── Egress IP ────────────────────────────────────────────────────────────────
public record WhoAmIResponse(
    string    ip,
    string    ipVersion,
    string    username,
    string?   email,
    bool      emailVerified,
    DateTime? subscriptionExpiresAt,
    int?      currentServerId,
    DateTime  observedAt);

public record SetSubscriptionRequest(DateTime expires_at);

public record PingResult(int id, string name, bool reachable, int? statusCode, string? error);

/// <summary>A user bound to a node, with the identity the node itself is keyed by.</summary>
public record BoundUser(int id, string username, Guid vpn_uuid);

/// <summary>
/// What an evacuation did. Deliberately a report rather than a bare 204: moving several
/// hundred users touches two nodes per user over the network, and some of it will fail.
/// </summary>
/// <param name="moved">Users now bound to a different node.</param>
/// <param name="stayed">Users the fleet had no room for — still on the old node.</param>
/// <param name="failed">Users moved in the database whose node calls did not both succeed.</param>
public record EvacuationReport(
    int serverId, int total, int moved, int stayed, int failed, IReadOnlyList<string> problems);

/// <summary><c>Code</c> is a stable machine-readable tag for clients that need to branch on the failure.</summary>
public record ApiError(string Message, string? Code = null);
