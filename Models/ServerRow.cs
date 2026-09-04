namespace HorusAPI.Models;

/// <summary>
/// Everything needed to serve one node's connection data, plus the shared secret used to
/// (de)provision a user on that node's agent.
///
/// The per-protocol columns are gone: what a client needs now lives entirely in
/// <see cref="offers"/>, the JSON the node reported. Nothing here has to change when a node
/// starts offering a new protocol — see <see cref="Services.OfferRenderer"/>.
/// </summary>
public record ServerRow(
    int     server_id,
    string  name,
    string  country,
    string  city,
    string  host,
    string  auth_password,

    /// <summary>The profile the node reports it is running (diagnostics, not used to build configs).</summary>
    string  profile,

    /// <summary>
    /// Raw JSON array of the node's offers, each a client-side xray outbound still carrying
    /// <c>${uuid}</c>. Kept as a string so this API never has to model a protocol's fields.
    /// </summary>
    string? offers);
