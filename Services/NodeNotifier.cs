using System.Net.Http.Json;
using HorusAPI.Models;

namespace HorusAPI.Services;

/// <summary>
/// Central → node control channel: (de)provisions a user's <c>vpn_uuid</c> on a node's
/// agent so xray's inbound <c>clients</c> array matches the DB binding. Best-effort and
/// idempotent — a node reconciles its full user set from its own store on restart, so a
/// dropped call self-heals; failures are logged, never thrown.
///
/// It also carries the user's monthly traffic across the move, which is what keeps a monthly
/// allowance from resetting on a change of server: POST /users hands the new node the month
/// so far (it restores it into xray before the user's first byte there), and the old node's
/// answer to DELETE /users/{uuid} brings back the bytes spent since its last telemetry post.
/// Every caller that moves a user goes through here, so none of them has to remember this.
/// </summary>
public interface INodeNotifier
{
    Task<bool> AddUserAsync(NodeTarget target, string uuid);
    Task<bool> RemoveUserAsync(NodeTarget target, string uuid);
}

public class NodeNotifier(
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ITrafficService traffic,
    ILogger<NodeNotifier> log) : INodeNotifier
{
    private int ControlPort => cfg.GetValue<int?>("Nodes:ControlPort") ?? 8444;
    private string Scheme   => cfg["Nodes:Scheme"] ?? "https";

    public async Task<bool> AddUserAsync(NodeTarget target, string uuid)
    {
        // A lookup that fails costs the user their carried-over month on this node, not their
        // connection: provisioning goes ahead without it, and the node's own reports take over.
        MonthUsage? usage = null;
        try
        {
            if (Guid.TryParse(uuid, out var id)) usage = await traffic.CurrentMonthAsync(id);
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not read {Uuid}'s month for the node: {Msg}", uuid, ex.Message);
        }

        var req = new HttpRequestMessage(HttpMethod.Post, $"{Scheme}://{target.Host}:{ControlPort}/users")
        {
            Content = JsonContent.Create(new { uuid, usage })
        };
        req.Headers.TryAddWithoutValidation(ApiConsts.API_HEADER, target.AuthPassword);
        return (await SendAsync(req, readBody: false)).ok;
    }

    public async Task<bool> RemoveUserAsync(NodeTarget target, string uuid)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{Scheme}://{target.Host}:{ControlPort}/users/{uuid}");
        req.Headers.TryAddWithoutValidation(ApiConsts.API_HEADER, target.AuthPassword);
        var (ok, body) = await SendAsync(req, readBody: true);

        // The user's month as they left this node. A node that predates it answers 204.
        if (body?.usage is { } usage)
        {
            try { await traffic.RecordAsync([new NodeUserUsage(uuid, usage.month, usage.total_bytes, usage.olcrtc_bytes)], null); }
            catch (Exception ex) { log.LogWarning("Could not record {Uuid}'s final month from {Host}: {Msg}", uuid, target.Host, ex.Message); }
        }
        return ok;
    }

    private async Task<(bool ok, NodeRemoveResponse? body)> SendAsync(HttpRequestMessage req, bool readBody)
    {
        var http = httpFactory.CreateClient("node");
        try
        {
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Node push to {Uri} → {Status}", req.RequestUri, (int)resp.StatusCode);
                return (false, null);
            }

            NodeRemoveResponse? body = null;
            if (readBody && resp.Content.Headers.ContentLength is not 0 && resp.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                try { body = await resp.Content.ReadFromJsonAsync<NodeRemoveResponse>(); }
                catch (Exception ex) { log.LogDebug("Node answer from {Uri} was not usage JSON: {Msg}", req.RequestUri, ex.Message); }
            }
            return (true, body);
        }
        catch (Exception ex)
        {
            log.LogError("Node push to {Uri} failed: {Msg}", req.RequestUri, ex.Message);
            return (false, null);
        }
    }
}
