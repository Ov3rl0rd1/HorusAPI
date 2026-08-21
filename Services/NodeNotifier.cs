using System.Net.Http.Json;

namespace HorusAPI.Services;

/// <summary>
/// Central → node control channel: (de)provisions a user's <c>vpn_uuid</c> on a node's
/// agent so xray's inbound <c>clients</c> array matches the DB binding. Best-effort and
/// idempotent — a node reconciles its full user set from its own store on restart, so a
/// dropped call self-heals; failures are logged, never thrown.
/// </summary>
public interface INodeNotifier
{
    Task<bool> AddUserAsync(NodeTarget target, string uuid);
    Task<bool> RemoveUserAsync(NodeTarget target, string uuid);
}

public class NodeNotifier(
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ILogger<NodeNotifier> log) : INodeNotifier
{
    private int ControlPort => cfg.GetValue<int?>("Nodes:ControlPort") ?? 8444;
    private string Scheme   => cfg["Nodes:Scheme"] ?? "https";

    public Task<bool> AddUserAsync(NodeTarget target, string uuid)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Scheme}://{target.Host}:{ControlPort}/users")
        {
            Content = JsonContent.Create(new { uuid })
        };
        req.Headers.TryAddWithoutValidation(ApiConsts.API_HEADER, target.AuthPassword);
        return SendOk(req);
    }

    public Task<bool> RemoveUserAsync(NodeTarget target, string uuid)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{Scheme}://{target.Host}:{ControlPort}/users/{uuid}");
        req.Headers.TryAddWithoutValidation(ApiConsts.API_HEADER, target.AuthPassword);
        return SendOk(req);
    }

    private async Task<bool> SendOk(HttpRequestMessage req)
    {
        var http = httpFactory.CreateClient("node");
        try
        {
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                log.LogWarning("Node push to {Uri} → {Status}", req.RequestUri, (int)resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            log.LogError("Node push to {Uri} failed: {Msg}", req.RequestUri, ex.Message);
            return false;
        }
    }
}
