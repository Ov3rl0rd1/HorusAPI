using System.Net;
using System.Net.Http.Json;
using HorusAPI.Models;

namespace HorusAPI.Services;

public interface INodeNotifier
{
    Task<bool> AddUserAsync(NodeTarget target, string email, string uuid);
    Task<bool> RemoveUserAsync(NodeTarget target, string email);
}

public class NodeNotifier(
    INodeService nodes,
    IHttpClientFactory httpFactory,
    IConfiguration cfg,
    ILogger<NodeNotifier> log) : INodeNotifier
{
    private int ControlPort => cfg.GetValue<int?>("Nodes:ControlPort") ?? 8444;
    private string Scheme   => cfg["Nodes:Scheme"] ?? "https";

    public async Task<bool> AddUserAsync(NodeTarget target, string email, string uuid)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Scheme}://{target.Host}:{ControlPort}/users")
        { 
            Content = JsonContent.Create(new { email = email, uuid = uuid })
        };
        req.Headers.TryAddWithoutValidation(ApiConsts.API_HEADER, target.AuthPassword);

        var res = await Send(req);

        return res.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveUserAsync(NodeTarget target, string email)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, $"{Scheme}://{target.Host}:{ControlPort}/users/{email}");
        req.Headers.TryAddWithoutValidation(ApiConsts.API_HEADER, target.AuthPassword);

        var res = await Send(req);

        return res.IsSuccessStatusCode;
    }

    private async Task<HttpResponseMessage> Send(HttpRequestMessage req)
    {
        var http = httpFactory.CreateClient("node");
        try
        {
            var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                log.LogWarning("Node push to {Host} → {Status}", req.RequestUri, (int)resp.StatusCode);

            return resp;
        }
        catch (Exception ex)
        {
            log.LogError("Node push to {Host} failed: {Msg}", req.RequestUri, ex.Message);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }
}
