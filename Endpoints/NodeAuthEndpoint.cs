using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace HorusAPI.Endpoints;

public static class NodeAuthEndpoints
{
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled);

    public static void MapNodeAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/node").WithTags("Auth").RequireRateLimiting("auth");

        // ── Auth ────────────────────────────────────────────────────────────────
        group.MapPost("/auth", async (
            [FromBody] LoginRequest req,
            IUserService userSvc,
            IJwtService jwtSvc,
            ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.username) ||
                (string.IsNullOrWhiteSpace(req.password) && string.IsNullOrWhiteSpace(req.session)))
                return Results.BadRequest(new ApiError("Username and password (or session) are required."));

            User? user;
            string? session;

            try
            {
                if (string.IsNullOrWhiteSpace(req.session))
                {
                    user = await userSvc.AuthenticateAsync(req.username, req.password);
                    if (user is null) return Results.Unauthorized();

                    session = await userSvc.CreateSession(req.username);
                    if (session is null) return Results.Problem("Session creation failed.", statusCode: 500);
                }
                else
                {
                    // Session login: validate existing session, reuse it
                    user = await userSvc.AuthenticateSessionAsync(req.username, req.session);
                    if (user is null) return Results.Unauthorized();
                    session = req.session;
                }
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            var (token, expires) = jwtSvc.Generate(user, session);
            log.LogInformation("User {Username} authenticated", user.username);
            return Results.Ok(new LoginResponse(token, expires, user.username, session));
        })
        .AllowAnonymous()
        .Produces<LoginResponse>(200)
        .Produces<ApiError>(400)
        .Produces(401);

        // ── Register ─────────────────────────────────────────────────────────────
        group.MapPost("/register", async (
            [FromBody] RegisterRequest req,
            IUserService userSvc,
            ILogger<Program> log) =>
        {
            string username = req.username ?? string.Empty;
            if (!UsernameRegex.IsMatch(username))
                return Results.BadRequest(new ApiError("Username must be 3–32 alphanumeric characters or underscores."));

            if (string.IsNullOrWhiteSpace(req.password) || req.password.Length < 8)
                return Results.BadRequest(new ApiError("Password must be at least 8 characters."));

            if (string.IsNullOrWhiteSpace(req.email))
                return Results.BadRequest(new ApiError("Email is required."));

            try
            {
                bool created = await userSvc.CreateUserAsync(username, req.password, req.email);
                if (!created)
                    return Results.Conflict(new ApiError("Username already taken."));
            }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            log.LogInformation("New user registered: {Username}", username);
            return Results.Created();
        })
        .AllowAnonymous()
        .Produces(201)
        .Produces<ApiError>(400)
        .Produces<ApiError>(409);

        // ── Logout other devices ─────────────────────────────────────────────────
        group.MapPost("/logout-others", async (
            [FromBody] LogoutOthersRequest req,
            HttpContext ctx,
            IUserService userSvc,
            ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.session))
                return Results.BadRequest(new ApiError("Current session token is required."));

            // Extract username from JWT unique_name claim
            string? username = ctx.User.FindFirstValue(ClaimTypes.Name)
                ?? ctx.User.FindFirstValue("unique_name");

            if (string.IsNullOrWhiteSpace(username))
                return Results.Unauthorized();

            try { await userSvc.ClearOtherSessionsAsync(username, req.session); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            log.LogInformation("User {Username} cleared other sessions", username);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .Produces(204)
        .Produces<ApiError>(400)
        .Produces(401);
    }
}