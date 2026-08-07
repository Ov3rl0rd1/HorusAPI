using HorusAPI.Models;
using HorusAPI.Services;

namespace HorusAPI.Tests.Unit;

public class ClientConfigBuilderTests
{
    private static ServerRow Server() => new(
        serverId:            1,
        host:                "node1.example.com",
        reality_public_key:  "PUBKEY123",
        reality_short_ids:   ["aa", "bb"],
        reality_server_name: "www.microsoft.com",
        reality_dest:        "www.microsoft.com:443",
        vless_port:          443,
        hysteria_port:       8443,
        hysteria_auth:       "hauth",
        hysteria_obfs:       "hobfs",
        hysteria_port_range: "20000-30000",
        olcrtc_provider:     "",
        olcrtc_transport:    "",
        olcrtc_room_id:      "",
        olcrtc_room_key:     "",
        agent_version:       "1.0.0");

    [Fact]
    public void MainVless_embeds_identity_endpoint_and_reality_params()
    {
        var uuid = Guid.NewGuid();

        string link = ClientConfigBuilder.MainVless(Server(), uuid);

        Assert.StartsWith($"vless://{uuid}@node1.example.com:443?", link);
        Assert.Contains("security=reality", link);
        Assert.Contains("pbk=PUBKEY123", link);
        Assert.Contains("sni=www.microsoft.com", link);
        Assert.EndsWith("#MainVLESS", link);
    }

    [Fact]
    public void MainHysteria_embeds_identity_port_and_obfs()
    {
        var uuid = Guid.NewGuid();

        string link = ClientConfigBuilder.MainHysteria(Server(), uuid);

        Assert.StartsWith($"hysteria2://{uuid}@node1.example.com:8443,20000-30000/?", link);
        Assert.Contains("obfs=salamander", link);
        Assert.Contains("obfs-password=hobfs", link);
        Assert.EndsWith("#MainHystria", link);
    }
}
