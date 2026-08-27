using System.Text;
using HorusAPI.Models;
using HorusAPI.Services;

namespace HorusAPI.Tests.Unit;

public class ClientConfigBuilderTests
{
    private static ServerRow Server() => new(
        server_id:           1,
        name:                "Germany 1",
        country:             "DE",
        city:                "Frankfurt",
        host:                "node1.example.com",
        auth_password:       "authpass",
        reality_public_key:  "PUBKEY123",
        reality_short_ids:   ["aa", "bb"],
        reality_server_name: "www.microsoft.com",
        reality_dest:        "www.microsoft.com:443",
        vless_port:          443,
        hysteria_port:       8443,
        obfs_password:       "hobfs",
        hop:                 "20000-30000",
        olcrtc_provider:     "",
        olcrtc_transport:    "",
        olcrtc_room_id:      "",
        olcrtc_room_key:     "",
        agent_version:       "1.0.0");

    [Fact]
    public void VlessLinks_embed_identity_endpoint_and_reality_params()
    {
        var uuid = Guid.NewGuid();

        string link = ClientConfigBuilder.VlessLinks(Server(), uuid)[0];

        Assert.StartsWith($"vless://{uuid}@node1.example.com:443?", link);
        Assert.Contains("security=reality", link);
        Assert.Contains("pbk=PUBKEY123", link);
        Assert.Contains("sni=www.microsoft.com", link);
        Assert.Contains("sid=aa", link);          // first short id
        Assert.Contains("Horus-DE", link);        // country tag
    }

    [Fact]
    public void Hysteria2Link_embeds_identity_port_and_obfs()
    {
        var uuid = Guid.NewGuid();

        string link = ClientConfigBuilder.Hysteria2Link(Server(), uuid);

        // Port hop lives in mport=…, not appended to the host, and there's no trailing "/".
        Assert.StartsWith($"hysteria2://{uuid}@node1.example.com:8443?sni=node1.example.com", link);
        Assert.DoesNotContain(":8443,", link);
        Assert.DoesNotContain("/?", link);
        Assert.Contains("obfs=salamander", link);
        Assert.Contains("mport=20000-30000", link);
        Assert.Contains("obfs-password=hobfs", link);
        Assert.Contains("Horus-DE", link);
    }

    [Fact]
    public void Hysteria2Link_normalises_a_colon_port_range_to_mport_dash()
    {
        var uuid = Guid.NewGuid();
        var s = Server() with { hop = "31111:49999" };

        string link = ClientConfigBuilder.Hysteria2Link(s, uuid);

        Assert.Contains("mport=31111-49999", link);
        Assert.DoesNotContain("31111:49999", link);
    }

    [Fact]
    public void Hysteria2Link_omits_mport_when_no_port_hop()
    {
        var uuid = Guid.NewGuid();
        var s = Server() with { hop = "" };

        string link = ClientConfigBuilder.Hysteria2Link(s, uuid);

        Assert.DoesNotContain("mport=", link);
        Assert.StartsWith($"hysteria2://{uuid}@node1.example.com:8443?sni=node1.example.com&obfs=salamander&obfs-password=hobfs", link);
    }

    [Fact]
    public void Subscription_is_base64_of_the_links()
    {
        var uuid = Guid.NewGuid();

        string sub = ClientConfigBuilder.Subscription(Server(), uuid);
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(sub));

        Assert.Contains("vless://", decoded);
        Assert.Contains("hysteria2://", decoded);
    }

    [Fact]
    public void OlcRtc_is_null_without_a_provider_and_populated_with_one()
    {
        var uuid = Guid.NewGuid();

        Assert.Null(ClientConfigBuilder.OlcRtc(Server(), uuid));

        var withRoom = Server() with
        {
            olcrtc_provider  = "webrtc",
            olcrtc_transport = "tcp",
            olcrtc_room_id   = "room-1",
            olcrtc_room_key  = "key-1"
        };
        var olc = ClientConfigBuilder.OlcRtc(withRoom, uuid);

        Assert.NotNull(olc);
        Assert.Equal(uuid.ToString(), olc!.uuid);
        Assert.Equal("webrtc", olc.provider);
        Assert.Equal("node1.example.com", olc.host);
    }
}
