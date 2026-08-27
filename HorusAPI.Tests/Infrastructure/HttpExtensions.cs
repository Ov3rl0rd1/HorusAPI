using System.Net.Http.Json;
using System.Text.Json;

namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Thin helpers to send requests with a chosen client IP (<c>X-Forwarded-For</c>) and
/// optional session header, and to read JSON fields out of the response.
/// </summary>
public static class HttpExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync(
        this HttpClient client, string url, object body, string ip, string? session = null)
        => client.SendAsync(Build(HttpMethod.Post, url, ip, session, body));

    public static Task<HttpResponseMessage> PutJsonAsync(
        this HttpClient client, string url, object body, string ip, string? session = null)
        => client.SendAsync(Build(HttpMethod.Put, url, ip, session, body));

    public static Task<HttpResponseMessage> GetWithAsync(
        this HttpClient client, string url, string ip, string? session)
        => client.SendAsync(Build(HttpMethod.Get, url, ip, session, body: null));

    public static Task<HttpResponseMessage> DeleteWithAsync(
        this HttpClient client, string url, string ip, string? session)
        => client.SendAsync(Build(HttpMethod.Delete, url, ip, session, body: null));

    private static HttpRequestMessage Build(HttpMethod method, string url, string ip, string? session, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        if (session is not null)
            request.Headers.TryAddWithoutValidation("X-Session-Key", session);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return request;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    public static async Task<string?> ReadStringPropAsync(this HttpResponseMessage response, string property)
    {
        var root = await response.ReadJsonAsync();
        return root.TryGetProperty(property, out var value) ? value.GetString() : null;
    }
}
