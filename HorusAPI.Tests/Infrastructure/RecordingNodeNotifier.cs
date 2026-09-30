using System.Collections.Concurrent;
using HorusAPI.Services;

namespace HorusAPI.Tests.Infrastructure;

/// <summary>
/// Stands in for the node control channel: records every add/remove instead of calling an agent.
/// The seeded hosts do not exist, so the real notifier spent its whole timeout on each call and
/// could not say what it would have sent. A host that starts with <c>down-</c> answers like an
/// unreachable agent (false), which is how the real one reports a failure.
/// </summary>
public sealed class RecordingNodeNotifier : INodeNotifier
{
    public sealed record Call(string Op, string Host, string Uuid);

    private readonly ConcurrentQueue<Call> _calls = new();

    public IReadOnlyList<Call> Calls => [.. _calls];

    public IEnumerable<Call> For(Guid uuid) => _calls.Where(c => c.Uuid == uuid.ToString());

    public Task<bool> AddUserAsync(NodeTarget target, string uuid) => Record("add", target, uuid);

    public Task<bool> RemoveUserAsync(NodeTarget target, string uuid) => Record("remove", target, uuid);

    private Task<bool> Record(string op, NodeTarget target, string uuid)
    {
        _calls.Enqueue(new Call(op, target.Host, uuid));
        return Task.FromResult(!target.Host.StartsWith("down-", StringComparison.Ordinal));
    }
}
