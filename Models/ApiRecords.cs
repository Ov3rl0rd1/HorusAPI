namespace HorusAPI.Models;

public record LoginRequest(string username, string password, string session);
public record LoginResponse(string session, DateTime? expiresAt, string username);
public record RegisterRequest(string username, string password, string email);
public record LogoutOthersRequest(string session);
public record ConnectResponse(string config);

public record SetSubscriptionRequest(DateTime expires_at);

public record PingResult(int id, string name, bool reachable, int? statusCode, string? error);

public record ApiError(string Message);