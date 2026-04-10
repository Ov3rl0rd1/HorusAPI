using System.Net;

namespace HorusAPI
{
    public class AuthMiddleware(RequestDelegate next, AppDbContext dbContext)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            if (context.Request.Headers.TryGetValue(ApiConsts.API_HEADER, out var api_key) == false)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsync("Unauthorized access!");
                return;
            }

            User? user = await dbContext.Users.FindAsync(api_key);


            if (user == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.Response.WriteAsync("Unauthorized access!");
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.OK;
            await context.Response.WriteAsync(user.ToString());

            await next(context);
        }
    }
}
