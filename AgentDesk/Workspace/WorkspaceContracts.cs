using JetBrains.Annotations;
using AgentDesk.Domain;
using System.Text.Json.Serialization;

namespace AgentDesk.Workspace;

/// <summary>A user-confirmed message to a bounded set of existing native threads.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DispatchRequest(string RequestKey, string[] ThreadIds, DispatchContent Content);

/// <summary>Durable native queue receipt for one recipient. Queued is not agent acknowledgement.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record DispatchDelivery(string Id, string BatchId, string ThreadId, string ThreadName,
    string WorkspacePath, string Message, DeliveryState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A batch retains one independent outcome per target.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record DispatchBatch(string Id, string RequestKey, IReadOnlyList<DispatchDelivery> Deliveries);

/// <summary>A shared operator/agent task, kept independently of the conversation's context window.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record WorkItem(string Id, string Title, string? Objective, string WorkspacePath,
    WorkOwner Owner, string? QuestionId, WorkState State,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version);

/// <summary>Create an operator or agent task associated with a project and optional question.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateWorkItemRequest(string Title, string WorkspacePath, WorkOwner Owner,
    string? Objective = null, string? QuestionId = null);

/// <summary>Update an existing task with a concurrency check and optional result evidence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateWorkItemRequest(int ExpectedVersion, WorkState State);

public interface IWorkspaceService
{
    Task<DispatchBatch> DispatchAsync(DispatchRequest request, CancellationToken ct);
    Task<IReadOnlyList<DispatchDelivery>> DeliveriesAsync(CancellationToken ct);
    Task<IReadOnlyList<WorkItem>> WorkItemsAsync(string? workspacePath, CancellationToken ct);
    Task<WorkItem> CreateWorkItemAsync(CreateWorkItemRequest request, CancellationToken ct);
    Task<WorkItem> UpdateWorkItemAsync(string id, UpdateWorkItemRequest request, CancellationToken ct);
}
