using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace HorusAPI.Services;

/// <summary>
/// OpenAPI wiring via the built-in <c>Microsoft.AspNetCore.OpenApi</c> generator.
/// This is for <b>local use only</b>:
/// <list type="bullet">
///   <item>The spec is written to a JSON file on disk at build time (see the
///   <c>OpenApiDocumentsDirectory</c> property in HorusAPI.csproj).</item>
///   <item>The interactive UI + JSON endpoint are mapped <b>only in the Development
///   environment</b>, so a Production container never serves them — and nginx does
///   not proxy <c>/swagger</c> or <c>/openapi</c> anyway.</item>
/// </list>
/// The built-in generator (not Swashbuckle) is used because its build-time tool
/// ships with the SDK and is version-matched to the runtime; Swashbuckle's own
/// build-time generator collides with its Microsoft.OpenApi dependency here.
/// Swashbuckle is kept only to render the local UI over the generated JSON.
/// </summary>
public static class OpenApiSetup
{
    public const string DocumentName = "v1";

    private const string SessionScheme = "SessionKey";
    private const string NodeScheme    = "NodePassword";

    public static void AddHorusOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title       = "HorusAPI",
                    Version     = "v1",
                    Description =
                        "VPN authentication & server-management API. Auth is a custom session " +
                        "scheme: obtain a token from /auth/login or /auth/verify and send it in " +
                        "the X-Session-Key header. Node routes authenticate with X-API-PASSWORD."
                };

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();

                // The custom session-header scheme (not JWT).
                document.Components.SecuritySchemes[SessionScheme] = new OpenApiSecurityScheme
                {
                    Name        = ApiConsts.SESSION_HEADER,
                    Type        = SecuritySchemeType.ApiKey,
                    In          = ParameterLocation.Header,
                    Description = "Session token issued by /auth/login or /auth/verify."
                };

                // Node ⇄ central shared secret.
                document.Components.SecuritySchemes[NodeScheme] = new OpenApiSecurityScheme
                {
                    Name        = ApiConsts.API_HEADER,
                    Type        = SecuritySchemeType.ApiKey,
                    In          = ParameterLocation.Header,
                    Description = "Per-node shared secret (vpn_servers.auth_password)."
                };

                return Task.CompletedTask;
            });

            // Attach the right scheme only to endpoints that actually require it,
            // so anonymous routes aren't misleadingly marked as secured.
            options.AddOperationTransformer((operation, context, _) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;

                // Explicitly anonymous → no requirement.
                if (metadata.OfType<IAllowAnonymous>().Any())
                    return Task.CompletedTask;

                string? scheme = null;

                if (metadata.OfType<IAuthorizeData>().Any())
                    scheme = SessionScheme;
                else if (context.Description.RelativePath?.StartsWith("node", StringComparison.OrdinalIgnoreCase) == true)
                    scheme = NodeScheme;   // node routes gate via an endpoint filter, not authorization metadata

                if (scheme is null) return Task.CompletedTask;

                // Link the reference to the host document so the requirement
                // serializes its scheme name (an unlinked reference emits "{}").
                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(scheme, context.Document)] = new List<string>()
                    }
                ];

                return Task.CompletedTask;
            });
        });
    }
}
