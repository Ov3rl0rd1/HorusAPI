using HorusAPI.Models;

namespace HorusAPI.Services
{
    public static class ClientConfigBuilder
    {
        private const string VLESS_ENCRYPTION = "none";
        private const string VLESS_FP = "randomized";

        public static string MainVless(ServerRow server, Guid uuid)
        {
            return $"vless://{uuid}@{server.host}:{server.vless_port}?encryption={VLESS_ENCRYPTION}&flow=xtls-rprx-vision&security=reality&sni={server.reality_server_name}&fp={VLESS_FP}&pbk={server.reality_public_key}&sid={server.reality_short_ids}&type=tcp#MainVLESS";
        }

        public static string MainHysteria(ServerRow server, Guid uuid)
        {
            return $"hysteria2://{uuid}@{server.host}:{server.hysteria_port},{server.hop}/?sni={server.host}&obfs=salamander&obfs-password={server.obfs_password}#MainHystria";
        }
    }
}
