namespace HorusAPI.Models;

public record LoginRequest(string username, string password);
public record LoginResponse(string session, DateTime? expiresAt);
public record RegisterRequest(string username, string password, string email);
public record LogoutOthersRequest();
public record ConnectResponse(string config);

public record SetSubscriptionRequest(DateTime expires_at);

public record PingResult(int id, string name, bool reachable, int? statusCode, string? error);

public record ApiError(string Message);