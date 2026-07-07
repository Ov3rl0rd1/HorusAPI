namespace HorusAPI
{
    public static class ApiConsts
    {
        public const string API_ROUTE = "/api";
        public const string LOGIN_ROUTE = "/login";
        public const string GET_SERVER = API_ROUTE + "/server_data";

        public const string APP_HEADER = "X-APP-DATA";
        public const string API_HEADER = "X-API-PASSWORD";
        public const string SESSION_HEADER = "X-Session-Key";

        public const string APP_DEFAULT_KEY = "VPlxhl1d8/kgxO1Nw8PsjMcMaCGpPI1CL3FUNILcY0";

        public const string SessionKeyClaimType = "SessionKey";
        public const string UserHttpContext = "User";

        public const string CONFIG_HOST = "host";
        public const string CONFIG_AUTH = "auth";
    }
}
