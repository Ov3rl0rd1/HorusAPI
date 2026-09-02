using System.Text;
using System.Text.Json.Nodes;
using HorusAPI.Models;
using HorusAPI.Services;
using Xunit;

namespace HorusAPI.Tests.Unit;

/// <summary>
/// The offers contract is what keeps this API protocol-agnostic: a node ships whole
/// client-side xray outbounds with a ${uuid} placeholder, and all the API does is substitute
/// the user. These tests pin that down — in particular that unknown JSON travels through
/// untouched, because that is the property a new protocol depends on.
/// </summary>
public class OfferRendererTests
{
    private static readonly Guid Uuid = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private const string TwoOffers = """
        [
          {
            "id": "vless-reality",
            "tag": "vless-in",
            "label": "VLESS Reality",
            "audience": ["app", "subscription"],
            "outbound": {
              "protocol": "vless",
              "settings": { "vnext": [ {
                  "address": "de1.example.com", "port": 443,
                  "users": [ { "id": "${uuid}", "flow": "xtls-rprx-vision" } ] } ] },
              "streamSettings": { "security": "reality",
                "realitySettings": { "publicKey": "PUB", "shortId": "abcd" } }
            },
            "uri": "vless://${uuid}@de1.example.com:443?pbk=PUB#${tag}"
          },
          {
            "id": "olcrtc",
            "tag": "olcrtc-in",
            "label": "olcRTC",
            "audience": ["app"],
            "outbound": { "protocol": "olcrtc",
              "settings": { "roomId": "room-42", "deviceId": "${uuid}" } }
          }
        ]
        """;

    /// <summary>A node with the two sample offers.</summary>
    private static ServerRow Server(string country = "DE") => ServerWith(TwoOffers, country);

    /// <summary>A node with exactly the given offers column — including null or nonsense.</summary>
    private static ServerRow ServerWith(string? offers, string country = "DE") => new(
        server_id:     7,
        name:          "de-1",
        country:       country,
        city:          "Berlin",
        host:          "de1.example.com",
        auth_password: "secret",
        profile:       "default",
        offers:        offers);

    // ── Substitution ─────────────────────────────────────────────────────────

    [Fact]
    public void The_users_uuid_is_substituted_wherever_it_appears()
    {
        var outbounds = OfferRenderer.RenderOutbounds(Server(), Uuid, OfferRenderer.AudienceApp);

        var vless = outbounds.Single(o => o.id == "vless-reality");
        Assert.Equal(Uuid.ToString(),
            vless.outbound["settings"]!["vnext"]![0]!["users"]![0]!["id"]!.GetValue<string>());

        var rtc = outbounds.Single(o => o.id == "olcrtc");
        Assert.Equal(Uuid.ToString(), rtc.outbound["settings"]!["deviceId"]!.GetValue<string>());
    }

    // The property a new protocol relies on: the API does not understand any of this and
    // must not touch it.
    [Fact]
    public void Everything_the_api_does_not_understand_travels_through_untouched()
    {
        const string exotic = """
            [ { "id": "future", "tag": "future-in", "audience": ["app"],
                "outbound": { "protocol": "some-protocol-invented-next-year",
                  "settings": { "nested": { "deep": [1, 2, { "flag": true, "id": "${uuid}" } ] },
                                "count": 42, "enabled": false, "nothing": null } } } ]
            """;

        var only = Assert.Single(OfferRenderer.RenderOutbounds(ServerWith(exotic), Uuid, OfferRenderer.AudienceApp));

        var settings = only.outbound["settings"]!;
        Assert.Equal(42, settings["count"]!.GetValue<int>());
        Assert.False(settings["enabled"]!.GetValue<bool>());
        Assert.Null(settings["nothing"]);
        Assert.True(settings["nested"]!["deep"]![2]!["flag"]!.GetValue<bool>());
        Assert.Equal(Uuid.ToString(), settings["nested"]!["deep"]![2]!["id"]!.GetValue<string>());
        Assert.Equal("some-protocol-invented-next-year", only.outbound["protocol"]!.GetValue<string>());
    }

    [Fact]
    public void Server_fields_are_substituted_too()
    {
        const string offers = """
            [ { "id": "x", "tag": "t", "audience": ["app"], "outbound": {
                  "host": "${host}", "country": "${country}", "city": "${city}",
                  "name": "${server_name}", "sid": "${server_id}" } } ]
            """;

        var only = Assert.Single(OfferRenderer.RenderOutbounds(ServerWith(offers), Uuid, OfferRenderer.AudienceApp));

        Assert.Equal("de1.example.com", only.outbound["host"]!.GetValue<string>());
        Assert.Equal("DE", only.outbound["country"]!.GetValue<string>());
        Assert.Equal("Berlin", only.outbound["city"]!.GetValue<string>());
        Assert.Equal("de-1", only.outbound["name"]!.GetValue<string>());
        Assert.Equal("7", only.outbound["sid"]!.GetValue<string>());
    }

    // An unknown placeholder belongs to the node, not to us. Blanking it would corrupt the
    // config quietly; leaving it makes the mistake visible.
    [Fact]
    public void An_unknown_placeholder_is_left_alone()
    {
        const string offers = """
            [ { "id": "x", "tag": "t", "audience": ["app"],
                "outbound": { "v": "${something_the_api_does_not_know}" } } ]
            """;

        var only = Assert.Single(OfferRenderer.RenderOutbounds(ServerWith(offers), Uuid, OfferRenderer.AudienceApp));

        Assert.Equal("${something_the_api_does_not_know}", only.outbound["v"]!.GetValue<string>());
    }

    [Fact]
    public void Labels_fall_back_to_the_offer_id()
    {
        const string offers = """[ { "id": "no-label", "tag": "t", "outbound": { "a": 1 } } ]""";

        var only = Assert.Single(OfferRenderer.RenderOutbounds(ServerWith(offers), Uuid, OfferRenderer.AudienceApp));

        Assert.Equal("no-label", only.label);
    }

    // ── Audience ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_subscription_audience_only_sees_what_declares_it()
    {
        var uris = OfferRenderer.RenderUris(Server(), Uuid, OfferRenderer.AudienceSubscription);

        // olcRTC is app-only and has no uri; VLESS declares both.
        var uri = Assert.Single(uris);
        Assert.StartsWith("vless://", uri);
    }

    [Fact]
    public void An_empty_audience_means_everyone()
    {
        const string offers = """
            [ { "id": "x", "tag": "t", "outbound": { "a": 1 }, "uri": "x://${uuid}" } ]
            """;

        Assert.Single(OfferRenderer.RenderOutbounds(ServerWith(offers), Uuid, OfferRenderer.AudienceApp));
        Assert.Single(OfferRenderer.RenderUris(ServerWith(offers), Uuid, OfferRenderer.AudienceSubscription));
    }

    [Fact]
    public void Offers_with_no_outbound_are_skipped()
    {
        const string offers = """
            [ { "id": "linkonly", "tag": "t", "audience": ["subscription"], "uri": "x://${uuid}" } ]
            """;

        Assert.Empty(OfferRenderer.RenderOutbounds(ServerWith(offers), Uuid, OfferRenderer.AudienceApp));
        Assert.Single(OfferRenderer.RenderUris(ServerWith(offers), Uuid, OfferRenderer.AudienceSubscription));
    }

    // ── URIs ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Uri_substitutions_are_escaped_because_they_land_in_a_query_or_fragment()
    {
        const string offers = """
            [ { "id": "x", "tag": "t", "audience": ["subscription"],
                "uri": "vless://${uuid}@${host}#${tag}" } ]
            """;

        // A country with a space would otherwise produce an invalid fragment.
        var uri = Assert.Single(OfferRenderer.RenderUris(ServerWith(offers, country: "Czech Republic"), Uuid,
            OfferRenderer.AudienceSubscription));

        Assert.EndsWith("#Horus-Czech%20Republic", uri);
    }

    [Fact]
    public void The_subscription_body_is_base64_of_newline_joined_links()
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
            OfferRenderer.Subscription(Server(), Uuid)));

        var lines = decoded.Split('\n');
        Assert.Single(lines);
        Assert.Contains(Uuid.ToString(), lines[0]);
    }

    // ── Degenerate input ─────────────────────────────────────────────────────

    // A node that has not registered since being upgraded has an empty column; /connect
    // turns "nothing to offer" into a 503, so this must not throw.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json at all")]
    [InlineData("""{ "not": "an array" }""")]
    public void Unusable_offers_yield_nothing_rather_than_throwing(string? offers)
    {
        Assert.Empty(OfferRenderer.RenderOutbounds(ServerWith(offers), Uuid, OfferRenderer.AudienceApp));
        Assert.Empty(OfferRenderer.RenderUris(ServerWith(offers), Uuid, OfferRenderer.AudienceSubscription));
    }

    [Fact]
    public void Offer_order_is_preserved_so_a_profile_can_state_a_preference()
    {
        var outbounds = OfferRenderer.RenderOutbounds(Server(), Uuid, OfferRenderer.AudienceApp);

        Assert.Equal(["vless-reality", "olcrtc"], outbounds.Select(o => o.id));
    }

    [Fact]
    public void Rendering_does_not_mutate_the_parsed_offers()
    {
        var server = Server();

        var first = OfferRenderer.RenderOutbounds(server, Uuid, OfferRenderer.AudienceApp);
        var other = Guid.Parse("99999999-9999-9999-9999-999999999999");
        var second = OfferRenderer.RenderOutbounds(server, other, OfferRenderer.AudienceApp);

        Assert.Equal(Uuid.ToString(),
            first[0].outbound["settings"]!["vnext"]![0]!["users"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(other.ToString(),
            second[0].outbound["settings"]!["vnext"]![0]!["users"]![0]!["id"]!.GetValue<string>());
    }
}
