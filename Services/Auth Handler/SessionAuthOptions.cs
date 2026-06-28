using Microsoft.AspNetCore.Authentication;

namespace HorusAPI.Services.Auth_Handler
{
    public class SessionAuthOptions : AuthenticationSchemeOptions
    {
        public const string SchemeName = "SessionHeaderScheme";
    }
}
