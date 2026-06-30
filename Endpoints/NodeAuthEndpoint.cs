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
    }
}