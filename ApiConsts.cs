namespace HorusAPI
{
    public static class ApiConsts
    {
        public const string API_ROUTE = "/api";
        public const string LOGIN_ROUTE = "/login";
        public const string GET_SERVER = API_ROUTE + "/server_data";

        public const string APP_HEADER = "X-APP-DATA";
        public const string API_HEADER = "X-API-PASSWORD";

        public const string APP_DEFAULT_KEY = "VPlxhl1d8/kgxO1Nw8PsjMcMaCGpPI1CL3FUNILcY0";

        public const string SUBSCRIPTION_EXPIRES_AT = "subscription_expires_at";
        public const string SESSION_ID = "session_id";

        public const string CONFIG_HOST = "host";
        public const string CONFIG_AUTH = "auth";
        public const string CONFIG_HOP_INTERVAL = "hop";
        public const string CONFIG_OBFS_TYPE = "obfs_type";
        public const string CONFIG_OBFS_PASSWORD = "obfs_password";
        public const string CONFIG_SOCKS_PASSWORD = "socks_password";
        public const string CONFIG_SOCKS_USERNAME = "socks_username";
        public const string CONFIG_SOCKS_PORT = "socks_port";

        public const string CONFIG_TEMPLATE = $"""
server: #{CONFIG_HOST}

auth: #{CONFIG_AUTH}

socks5:
  listen: 127.0.0.1:#{CONFIG_SOCKS_PORT}
  username: #{CONFIG_SOCKS_USERNAME}
  password: #{CONFIG_SOCKS_PASSWORD}

quic:
  maxIdleTimeout: 30s 
  keepAlivePeriod: 20s

transport:
  udp:
    hopInterval: #{CONFIG_HOP_INTERVAL}

#???{CONFIG_OBFS_TYPE}
obfs:
  type: salamander
  salamander:
    password: #{CONFIG_OBFS_PASSWORD}
#???
""";
    }
}
