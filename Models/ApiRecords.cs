namespace HorusAPI.Models;

public record LoginRequest(string username, string password);
public record LoginResponse(string session, DateTime? expiresAt);
public record RegisterRequest(string username, string password, string email);
public record LogoutOthersRequest();

// ── E-mail confirmation ──────────────────────────────────────────────────────
public record VerifyRequest(string email, string code);
public record ResendCodeRequest(string email);

/// <summary>Answer to /auth/register: the account exists but cannot log in until the code is entered.</summary>
public record RegisterResponse(string status, string email, int codeExpiresInSeconds);

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

/// <summary><c>Code</c> is a stable machine-readable tag for clients that need to branch on the failure.</summary>
public record ApiError(string Message, string? Code = null);
