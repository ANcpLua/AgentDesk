using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AgentDesk.Codex;
using AgentDesk.Questions;
using AgentDesk.Workspace;
using AgentDesk.Domain;

namespace AgentDesk.Mcp;

[McpServerToolType]
public sealed class WorkspaceTools(ICodexClient codex, IWorkspaceService workspace)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [McpServerTool(Name = "read_codex_usage", ReadOnly = true, OpenWorld = false)]
    [Description("Read current account-wide Codex usage and reset times. Unavailable fields are not zero.")]
    public Task<CallToolResult> Usage(CancellationToken cancellationToken = default) =>
        Result(async () => await codex.UsageAsync(cancellationToken));
    [McpServerTool(Name = "list_codex_threads", ReadOnly = true, OpenWorld = false)]
    [Description("Read a bounded page of existing native Codex threads with verified IDs, project paths and runtime status. Does not resume threads or load full conversations.")]
    public Task<CallToolResult> Threads(string? workspacePath = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        Result(async () => await codex.ListAsync(workspacePath, cursor, cancellationToken));

    [McpServerTool(Name = "read_codex_thread", ReadOnly = true, OpenWorld = false)]
    [Description("Read one native thread's identity, current goal and latest bounded reply summary. This is thread activity, not proof that a particular message was acknowledged.")]
    public Task<CallToolResult> Thread(string threadId, CancellationToken cancellationToken = default) =>
        Result(async () => await codex.DetailAsync(threadId, cancellationToken));

    [McpServerTool(Name = "dispatch_to_codex", Destructive = false, OpenWorld = false)]
    [McpMeta("ui", JsonValue = """{"visibility":["app"]}""")]
    [Description("User-operated send action. Queue the operator's explicit message or a saved human answer to 1-8 selected native Codex thread IDs. requestKey prevents duplicate sends. Queued means accepted by codex queue, not answered.")]
    public Task<CallToolResult> Dispatch(string requestKey, string[] threadIds, DispatchContent content,
        CancellationToken cancellationToken = default) =>
        Result(async () => await workspace.DispatchAsync(new(requestKey, threadIds, content), cancellationToken));

    [McpServerTool(Name = "list_dispatches", ReadOnly = true, OpenWorld = false)]
    [Description("Read the latest 100 individual native queue receipts. Uncertain deliveries must not be automatically retried.")]
    public Task<CallToolResult> Deliveries(CancellationToken cancellationToken = default) =>
        Result(async () => new { deliveries = await workspace.DeliveriesAsync(cancellationToken) });

    [McpServerTool(Name = "list_work_items", ReadOnly = true, OpenWorld = false)]
    [Description("Read shared operator and agent tasks, goals, blockers and recorded outcomes for a workspace. Prefer these compact records over replaying full chat history.")]
    public Task<CallToolResult> WorkItems(string? workspacePath = null, CancellationToken cancellationToken = default) =>
        Result(async () => new { items = await workspace.WorkItemsAsync(workspacePath, cancellationToken) });

    [McpServerTool(Name = "create_work_item", Destructive = false, OpenWorld = false)]
    [Description("Record a concrete shared task. owner is {kind:operator} or {kind:agent,threadId:...}. Creating a task does not start an agent or authorize unrelated actions.")]
    public Task<CallToolResult> CreateWorkItem(string title, string workspacePath, WorkOwner owner,
        string? objective = null, string? questionId = null, CancellationToken cancellationToken = default) =>
        Result(async () => await workspace.CreateWorkItemAsync(new(title, workspacePath, owner, objective, questionId), cancellationToken));

    [McpServerTool(Name = "update_work_item", Destructive = false, OpenWorld = false)]
    [Description("Update a shared task with expectedVersion and a state object. kind: todo/in_progress with optional note; blocked with reason; done with evidence. Never treat pending human input as approval.")]
    public Task<CallToolResult> UpdateWorkItem(string id, int expectedVersion, WorkState state, CancellationToken cancellationToken = default) =>
        Result(async () => await workspace.UpdateWorkItemAsync(id, new(expectedVersion, state), cancellationToken));

    private static async Task<CallToolResult> Result<T>(Func<Task<T>> action)
    {
        try
        {
            var data = JsonSerializer.SerializeToElement(await action(), Json);
            return new()
            {
                StructuredContent = data,
                Content = [new TextContentBlock { Text = data.GetRawText() }]
            };
        }
        catch (Exception e) when (e is QuestionException or CodexConnectionException)
        {
            return new()
            {
                IsError = true,
                Content = [new TextContentBlock { Text = e.Message }]
            };
        }
    }
}
