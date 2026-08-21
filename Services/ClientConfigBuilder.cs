using System.Text;
using HorusAPI.Models;

namespace HorusAPI.Services;

/// <summary>
/// Turns a <see cref="ServerRow"/> + a user's <c>vpn_uuid</c> into ready-to-use client
/// links. Pure string building — no I/O, no allocation beyond the strings themselves —
/// so /connect stays cheap under load.
/// </summary>
public static class ClientConfigBuilder
{
    private const string VlessEncryption = "none";
    private const string VlessFingerprint = "randomized";

    /// <summary>Every VLESS variant the node exposes (one today: Reality + Vision + TCP).</summary>
    public static string[] VlessLinks(ServerRow s, Guid uuid)
    {
        string sid = s.reality_short_ids is { Length: > 0 } ? s.reality_short_ids[0] : string.Empty;
        string tag = Tag(s, "VLESS");

        return
        [
            $"vless://{uuid}@{s.host}:{s.vless_port}" +
            $"?encryption={VlessEncryption}&flow=xtls-rprx-vision&security=reality" +
            $"&sni={s.reality_server_name}&fp={VlessFingerprint}" +
            $"&pbk={s.reality_public_key}&sid={sid}&type=tcp#{tag}"
        ];
    }

    public static string Hysteria2Link(ServerRow s, Guid uuid)
    {
        string hop = string.IsNullOrEmpty(s.hop) ? string.Empty : "," + s.hop;
        return $"hysteria2://{uuid}@{s.host}:{s.hysteria_port}{hop}/" +
               $"?sni={s.host}&obfs=salamander&obfs-password={s.obfs_password}#{Tag(s, "HY2")}";
    }

    /// <summary>olcRTC parameters, or null when the node advertises no room (provider empty).</summary>
    public static OlcRtc? OlcRtc(ServerRow s, Guid uuid) =>
        string.IsNullOrEmpty(s.olcrtc_provider)
            ? null
            : new OlcRtc(s.olcrtc_provider, s.olcrtc_transport, s.olcrtc_room_id, s.olcrtc_room_key,
                         uuid.ToString(), s.host);

    /// <summary>Third-party subscription body: base64 of the newline-joined links
    /// (VLESS variants + Hysteria2). This is what v2rayN/Hiddify/Streisand import from a
    /// subscription URL. olcRTC is intentionally excluded — it's app-only.</summary>
    public static string Subscription(ServerRow s, Guid uuid)
    {
        var links = new List<string>(VlessLinks(s, uuid)) { Hysteria2Link(s, uuid) };
        string joined = string.Join('\n', links);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(joined));
    }

    /// <summary>Human-readable label shown in the client (e.g. "Horus-DE · VLESS").</summary>
    private static string Tag(ServerRow s, string proto)
    {
        string where = !string.IsNullOrWhiteSpace(s.country) ? s.country
                     : !string.IsNullOrWhiteSpace(s.name)    ? s.name
                     : "Horus";
        return Uri.EscapeDataString($"Horus-{where} · {proto}");
    }
}
