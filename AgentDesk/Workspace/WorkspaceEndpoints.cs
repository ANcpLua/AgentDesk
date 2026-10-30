using AgentDesk.Codex;

namespace AgentDesk.Workspace;

public static class WorkspaceEndpoints
{
    public static void MapWorkspace(this WebApplication app)
    {
        var codex = app.MapGroup("/api/codex").WithTags("Codex");
        codex.MapGet("/hooks", async (string? workspacePath, ICodexClient client, CancellationToken ct) =>
            TypedResults.Ok(await client.HooksAsync(workspacePath, ct)))
            .WithName("ListCodexHooks").WithSummary("Read configured native hooks, diagnostics and trust status without granting trust or running hooks.");
        codex.MapGet("/token-activity", async (ICodexClient client, CancellationToken ct) =>
            TypedResults.Ok(await client.TokenActivityAsync(ct)))
            .WithName("ReadCodexTokenActivity").WithSummary("Read account-wide daily token activity; counters are decimal strings and unavailable measurements are null.");
        codex.MapGet("/mcp-servers", async (string? threadId, string? serverName, string? cursor, ICodexClient client, CancellationToken ct) =>
            TypedResults.Ok(await client.McpServersAsync(threadId, serverName, cursor, ct)))
            .WithName("ListCodexMcpServers").WithSummary("Read a bounded MCP inventory page and optional thread-scoped runtime connection states.");
        codex.MapGet("/usage", async (ICodexClient client, CancellationToken ct) =>
            TypedResults.Ok(await client.UsageAsync(ct)))
            .WithName("ReadCodexUsage").WithSummary("Read current account usage from the Codex app server.");
        codex.MapGet("/threads", async (string? workspacePath, string? cursor, ICodexClient client, CancellationToken ct) =>
            TypedResults.Ok(await client.ListAsync(workspacePath, cursor, ct)))
            .WithName("ListCodexThreads").WithSummary("Read a bounded page from the native Codex agent list.");
        codex.MapGet("/threads/{id}", async (string id, ICodexClient client, CancellationToken ct) =>
            TypedResults.Ok(await client.DetailAsync(id, ct)))
            .WithName("ReadCodexThread").WithSummary("Read thread identity, goal and most recent summary without resuming it.");
        codex.MapPost("/dispatch", async (DispatchRequest request, IWorkspaceService service, CancellationToken ct) =>
            TypedResults.Ok(await service.DispatchAsync(request, ct)))
            .WithName("DispatchToCodexThreads").WithSummary("Queue an explicitly composed message to selected thread IDs.");
        codex.MapGet("/dispatch", async (IWorkspaceService service, CancellationToken ct) =>
            TypedResults.Ok(new
            {
                deliveries = await service.DeliveriesAsync(ct)
            }))
            .WithName("ListCodexDeliveries").WithSummary("Read native queue receipts and uncertain or failed deliveries.");
        var tasks = app.MapGroup("/api/work-items").WithTags("Work items");
        tasks.MapGet("/", async (string? workspacePath, IWorkspaceService service, CancellationToken ct) =>
            TypedResults.Ok(new
            {
                items = await service.WorkItemsAsync(workspacePath, ct)
            }))
            .WithName("ListWorkItems").WithSummary("Read operator and agent tasks.");
        tasks.MapGet("/{id}", async (string id, IWorkspaceService service, CancellationToken ct) =>
        {
            var item = (await service.WorkItemsAsync(null, ct)).FirstOrDefault(x => x.Id == id)
                ?? throw new Questions.QuestionException(404, "Aufgabe nicht gefunden.");
            return TypedResults.Ok(item);
        }).WithName("GetWorkItem").WithSummary("Read one shared task.");
        tasks.MapPost("/", async (CreateWorkItemRequest request, IWorkspaceService service, CancellationToken ct) =>
        {
            var item = await service.CreateWorkItemAsync(request, ct);
            return TypedResults.Created($"/api/work-items/{item.Id}", item);
        }).WithName("CreateWorkItem").WithSummary("Record a shared task and responsible operator or agent.");
        tasks.MapPut("/{id}", async (string id, UpdateWorkItemRequest request, IWorkspaceService service, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateWorkItemAsync(id, request, ct)))
            .WithName("UpdateWorkItem").WithSummary("Update progress, retaining a concrete result on completion.");
    }
}
