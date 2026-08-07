using System.Collections.Concurrent;
using HorusAPI.Services;

namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Test double for <see cref="IEmailSender"/>. Instead of sending, it records the
/// last verification code and reset link per address so tests can complete the
/// flows (the real code is hashed in the DB and cannot be read back).
/// </summary>
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly ConcurrentDictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> SendVerificationCodeAsync(string email, string username, string code, TimeSpan validFor, CancellationToken ct = default)
    {
        _codes[email.Trim()] = code;
        return Task.FromResult(true);
    }

    public Task<bool> SendPasswordResetAsync(string email, string username, string link, TimeSpan validFor, CancellationToken ct = default)
    {
        _links[email.Trim()] = link;
        return Task.FromResult(true);
    }

    public string? LastCodeFor(string email) => _codes.TryGetValue(email.Trim(), out var c) ? c : null;

    public string? LastLinkFor(string email) => _links.TryGetValue(email.Trim(), out var l) ? l : null;

    public bool WasAnythingSentTo(string email) => _codes.ContainsKey(email.Trim()) || _links.ContainsKey(email.Trim());
}
