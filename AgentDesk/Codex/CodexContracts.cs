using JetBrains.Annotations;
using AgentDesk.Domain;

namespace AgentDesk.Codex;

/// <summary>Verified metadata from the running Codex app server, without a copied chat history.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexThread(
    string Id, string SessionId, string Name, string WorkspacePath, string Status, string[] ActiveFlags,
    string? Model, string? ReasoningEffort, string? ParentThreadId, string? ForkedFromId,
    string? TranscriptPath, string? Originator, string? AgentRole, DateTimeOffset UpdatedAt);

/// <summary>One bounded page of native Codex threads.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexThreadPage(IReadOnlyList<CodexThread> Threads, string? NextCursor);

/// <summary>The latest bounded summary, shown as thread activity, not as a delivery receipt.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexThreadDetail(CodexThread Thread, string? Goal, string? GoalStatus,
    string? LatestTurnId, string? LatestTurnStatus, string? LatestReply);

public interface ICodexClient
{
    Task<CodexThreadPage> ListAsync(string? workspacePath, string? cursor, CancellationToken ct);
    Task<CodexThread> GetAsync(string threadId, CancellationToken ct);
    Task<CodexThreadDetail> DetailAsync(string threadId, CancellationToken ct);
    Task<QueueOutcome> QueueAsync(string threadId, string message, CancellationToken ct);
}

public sealed class CodexConnectionException(string message) : Exception(message);
