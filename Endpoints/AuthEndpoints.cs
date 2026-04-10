using Microsoft.AspNetCore.Mvc;
using HorusAPI.Models;
using HorusAPI.Services;

namespace HorusAPI.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/auth")
            .WithTags("Auth");

        // POST /auth/login
        group.MapPost("/login", async (
            [FromBody] LoginRequest req,
            IUserService userSvc,
            IJwtService  jwtSvc,
            ILogger<Program> log) =>
        {
            if (string.IsNullOrWhiteSpace(req.username) ||
                string.IsNullOrWhiteSpace(req.password))
                return Results.BadRequest(new ApiError("Username and password are required."));

            User? user;
            try { user = await userSvc.AuthenticateAsync(req.username, req.password); }
            catch { return Results.Problem("Database error.", statusCode: 503); }

            if (user is null)
                return Results.Unauthorized();

            var (token, expires) = jwtSvc.Generate(user);
            log.LogInformation($"User {user.username} authenticated");

            return Results.Ok(new LoginResponse(token, expires, user.username, user.api_key));
        })
        .AllowAnonymous()
        .Produces<LoginResponse>(200)
        .Produces<ApiError>(400)
        .Produces(401);
    }
}
