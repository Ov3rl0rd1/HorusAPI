using System.Security.Claims;

namespace HorusAPI.Services.Auth_Handler
{
    public static class ClaimsPrincipalExtensions
    {
        public static string? GetSessionKey(this ClaimsPrincipal user)
        {
            if (user?.Identity == null || !user.Identity.IsAuthenticated)
                return null;

            return user.FindFirst(ApiConsts.SessionKeyClaimType)?.Value;
        }
    }
}
