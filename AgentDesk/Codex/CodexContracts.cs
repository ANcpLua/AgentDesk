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

/// <summary>A usage window reported by the Codex account service.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexUsageWindow(double UsedPercent, long? WindowDurationMins, long? ResetsAt);
/// <summary>Credit availability reported alongside account usage.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexCredits(bool HasCredits, bool Unlimited, string? Balance);
/// <summary>A single metered account usage bucket.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexUsageBucket(string? LimitId, string? LimitName, CodexUsageWindow? Primary,
    CodexUsageWindow? Secondary, CodexCredits? Credits);
/// <summary>Current account usage. Null fields represent unavailable information.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexUsage(bool? OrdinaryUsageAllowed, CodexUsageBucket? RateLimits,
    Dictionary<string, CodexUsageBucket>? RateLimitsByLimitId);

public interface ICodexClient
{
    Task<CodexUsage> UsageAsync(CancellationToken ct);
    Task<CodexThreadPage> ListAsync(string? workspacePath, string? cursor, CancellationToken ct);
    Task<CodexThread> GetAsync(string threadId, CancellationToken ct);
    Task<CodexThreadDetail> DetailAsync(string threadId, CancellationToken ct);
    Task<QueueOutcome> QueueAsync(string threadId, string message, CancellationToken ct);
}

public sealed class CodexConnectionException(string message) : Exception(message);
