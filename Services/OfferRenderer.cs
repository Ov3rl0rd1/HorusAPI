using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HorusAPI.Models;

namespace HorusAPI.Services;

/// <summary>
/// Turns the offers a node reported into one user's client configuration.
///
/// This class deliberately knows nothing about VLESS, Hysteria, olcRTC or anything else. A
/// node ships whole client-side xray outbounds with a <c>${uuid}</c> placeholder left in;
/// all this does is walk the JSON, substitute the handful of values only central knows, and
/// hand the result back. That is what lets a node start offering a protocol this API has never
/// heard of without a single change here.
///
/// Substituted: <c>${uuid}</c>, <c>${tag}</c>, <c>${server_name}</c>, <c>${country}</c>,
/// <c>${city}</c>, <c>${host}</c>, <c>${server_id}</c>. Inside a <c>uri</c> the values are
/// URI-escaped (they end up in a query string or a fragment); inside <c>outbound</c> they are
/// inserted raw.
///
/// Pure string/JSON work — no I/O — so /connect stays cheap under load.
/// </summary>
public static class OfferRenderer
{
    private static readonly Regex Placeholder = new(@"\$\{([a-z_]+)\}", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions Lenient = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public const string AudienceApp = "app";
    public const string AudienceSubscription = "subscription";

    /// <summary>One offer as it left the node, before the user is substituted in.</summary>
    public sealed class Offer
    {
        public string Id { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Label { get; set; } = "";
        public string[] Audience { get; set; } = [];
        public JsonNode? Outbound { get; set; }
        public string? Uri { get; set; }

        /// <summary>An empty audience means "everyone" — do not make a node spell both out.</summary>
        public bool VisibleTo(string audience) =>
            Audience.Length == 0 || Audience.Contains(audience, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse the JSONB column. Returns an empty list for anything unusable rather than
    /// throwing: a malformed row must degrade to "this node has nothing to offer", which the
    /// caller reports as a server problem, not a 500 on a user's connect.
    /// </summary>
    public static List<Offer> Parse(string? offersJson)
    {
        if (string.IsNullOrWhiteSpace(offersJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<Offer>>(offersJson, Lenient) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Render every offer visible to <paramref name="audience"/> for one user.
    /// Offers with no outbound are skipped — there would be nothing to hand the client.
    /// </summary>
    public static List<ClientOutbound> RenderOutbounds(ServerRow server, Guid uuid, string audience)
    {
        var values = Values(server, uuid);
        var result = new List<ClientOutbound>();

        foreach (var offer in Parse(server.offers))
        {
            if (!offer.VisibleTo(audience) || offer.Outbound is null) continue;

            var outbound = Substitute(offer.Outbound, values, escape: false);
            if (outbound is null) continue;

            result.Add(new ClientOutbound(
                id:       offer.Id,
                label:    string.IsNullOrWhiteSpace(offer.Label) ? offer.Id : offer.Label,
                tag:      offer.Tag,
                outbound: outbound));
        }

        return result;
    }

    /// <summary>
    /// The share links for the third-party subscription path. Only offers that declare a
    /// <c>uri</c> take part: writing one is how a profile opts a protocol into subscriptions,
    /// and protocols with no share-link format (olcRTC) simply do not have one.
    /// </summary>
    public static List<string> RenderUris(ServerRow server, Guid uuid, string audience)
    {
        var values = Values(server, uuid);

        return [.. Parse(server.offers)
            .Where(o => o.VisibleTo(audience) && !string.IsNullOrWhiteSpace(o.Uri))
            .Select(o => Interpolate(o.Uri!, values, escape: true))];
    }

    /// <summary>base64 of the newline-joined links — what v2rayN/Hiddify import.</summary>
    public static string Subscription(ServerRow server, Guid uuid)
    {
        var links = RenderUris(server, uuid, AudienceSubscription);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', links)));
    }

    // ── Substitution ─────────────────────────────────────────────────────────

    private static Dictionary<string, string> Values(ServerRow s, Guid uuid) => new(StringComparer.Ordinal)
    {
        ["uuid"]        = uuid.ToString(),
        ["host"]        = s.host ?? "",
        ["country"]     = s.country ?? "",
        ["city"]        = s.city ?? "",
        ["server_name"] = s.name ?? "",
        ["server_id"]   = s.server_id.ToString(),
        // Human-readable label for the client, e.g. "Horus-DE". Offers append their own
        // protocol name, so this stays the location rather than a full title.
        ["tag"]         = Tag(s),
    };

    private static string Tag(ServerRow s) =>
        !string.IsNullOrWhiteSpace(s.country) ? $"Horus-{s.country}"
      : !string.IsNullOrWhiteSpace(s.name)    ? $"Horus-{s.name}"
      : "Horus";

    /// <summary>Deep-copy a JSON tree with the placeholders in its strings substituted.</summary>
    private static JsonNode? Substitute(JsonNode? node, Dictionary<string, string> values, bool escape)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj) result[key] = Substitute(value, values, escape);
                return result;
            }

            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr) result.Add(Substitute(item, values, escape));
                return result;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Interpolate(text, values, escape));

            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// Replace known placeholders in one string. An unknown <c>${…}</c> is left exactly as it
    /// is: it belongs to the node, not to us, and blanking it would quietly corrupt a config
    /// rather than making the problem visible.
    /// </summary>
    private static string Interpolate(string text, Dictionary<string, string> values, bool escape)
    {
        if (!text.Contains("${", StringComparison.Ordinal)) return text;

        return Placeholder.Replace(text, match =>
        {
            if (!values.TryGetValue(match.Groups[1].Value, out var value)) return match.Value;
            return escape ? Uri.EscapeDataString(value) : value;
        });
    }
}
