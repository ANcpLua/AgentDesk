using JetBrains.Annotations;
using AgentDesk.Codex;
using AgentDesk.Infrastructure;
using AgentDesk.Domain;

namespace AgentDesk.Workspace;

public sealed class HttpWorkspaceClient(InboxHttpTransport transport) : IWorkspaceService, ICodexClient
{
    public Task<CodexHooks> HooksAsync(string? workspacePath, CancellationToken ct) =>
        Send<CodexHooks>(HttpMethod.Get, "/api/codex/hooks" + (workspacePath is null ? "" : "?workspacePath=" + Uri.EscapeDataString(workspacePath)), null, ct);
    public Task<CodexMcpServerPage> McpServersAsync(string? threadId, string? serverName, string? cursor, CancellationToken ct)
    {
        var query = new List<string>();
        if (threadId is not null) query.Add("threadId=" + Uri.EscapeDataString(threadId));
        if (serverName is not null) query.Add("serverName=" + Uri.EscapeDataString(serverName));
        if (cursor is not null) query.Add("cursor=" + Uri.EscapeDataString(cursor));
        return Send<CodexMcpServerPage>(HttpMethod.Get, "/api/codex/mcp-servers?" + string.Join("&", query), null, ct);
    }
    public Task<CodexUsage> UsageAsync(CancellationToken ct) => Send<CodexUsage>(HttpMethod.Get, "/api/codex/usage", null, ct);
    public Task<CodexTokenActivity> TokenActivityAsync(CancellationToken ct) => Send<CodexTokenActivity>(HttpMethod.Get, "/api/codex/token-activity", null, ct);
    public Task<CodexThreadPage> ListAsync(string? path, string? cursor, CancellationToken ct)
    {
        var query = new List<string>();
        if (path is not null)
            query.Add("workspacePath=" + Uri.EscapeDataString(path));
        if (cursor is not null)
            query.Add("cursor=" + Uri.EscapeDataString(cursor));
        return Send<CodexThreadPage>(HttpMethod.Get, "/api/codex/threads?" + string.Join("&", query), null, ct);
    }
    public async Task<CodexThread> GetAsync(string id, CancellationToken ct) => (await DetailAsync(id, ct)).Thread;
    public Task<CodexThreadDetail> DetailAsync(string id, CancellationToken ct) =>
        Send<CodexThreadDetail>(HttpMethod.Get, "/api/codex/threads/" + Uri.EscapeDataString(id), null, ct);
    public Task<QueueOutcome> QueueAsync(string id, string message, CancellationToken ct) => throw new NotSupportedException("Use the durable dispatch endpoint.");
    public Task<DispatchBatch> DispatchAsync(DispatchRequest request, CancellationToken ct) => Send<DispatchBatch>(HttpMethod.Post, "/api/codex/dispatch", request, ct);
    public async Task<IReadOnlyList<DispatchDelivery>> DeliveriesAsync(CancellationToken ct) => (await Send<DeliveryEnvelope>(HttpMethod.Get, "/api/codex/dispatch", null, ct)).Deliveries;
    public async Task<IReadOnlyList<WorkItem>> WorkItemsAsync(string? path, CancellationToken ct) => (await Send<WorkEnvelope>(HttpMethod.Get, "/api/work-items/" + (path is null ? "" : "?workspacePath=" + Uri.EscapeDataString(path)), null, ct)).Items;
    public Task<WorkItem> CreateWorkItemAsync(CreateWorkItemRequest request, CancellationToken ct) => Send<WorkItem>(HttpMethod.Post, "/api/work-items/", request, ct);
    public Task<WorkItem> UpdateWorkItemAsync(string id, UpdateWorkItemRequest request, CancellationToken ct) => Send<WorkItem>(HttpMethod.Put, "/api/work-items/" + Uri.EscapeDataString(id), request, ct);
    private Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        transport.SendAsync<T>(method, path, body, ct);
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record DeliveryEnvelope(DispatchDelivery[] Deliveries);
    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed record WorkEnvelope(WorkItem[] Items);
}
