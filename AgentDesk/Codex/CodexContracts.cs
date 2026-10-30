using JetBrains.Annotations;
using AgentDesk.Domain;
using System.Text.Json.Serialization;

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
public sealed record CodexUsageWindow([property: JsonRequired] double UsedPercent, long? WindowDurationMins, long? ResetsAt);
/// <summary>Credit availability reported alongside account usage.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexCredits([property: JsonRequired] bool HasCredits, [property: JsonRequired] bool Unlimited, string? Balance);
/// <summary>A single metered account usage bucket.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexUsageBucket(string? LimitId, string? LimitName, CodexUsageWindow? Primary,
    CodexUsageWindow? Secondary, CodexCredits? Credits);
/// <summary>Current account usage. Null fields represent unavailable information.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexUsage(bool? OrdinaryUsageAllowed, CodexUsageBucket? RateLimits,
    Dictionary<string, CodexUsageBucket>? RateLimitsByLimitId);

/// <summary>One server's reported runtime state. A missing state is unknown, and tool discovery is independent.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexMcpServer(string Name, string? RuntimeStatus, string AuthStatus,
    int? ToolCount, string? ToolsError, string? HttpOrigin, string? PluginId);

/// <summary>A bounded native MCP inventory page; runtime states are scoped to the requested thread.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexMcpServerPage(IReadOnlyList<CodexMcpServer> Servers, string? NextCursor);

public interface ICodexClient
{
    Task<CodexUsage> UsageAsync(CancellationToken ct);
    Task<CodexHooks> HooksAsync(string? workspacePath, CancellationToken ct);
    Task<CodexTokenActivity> TokenActivityAsync(CancellationToken ct);
    Task<CodexMcpServerPage> McpServersAsync(string? threadId, string? serverName, string? cursor, CancellationToken ct);
    Task<CodexThreadPage> ListAsync(string? workspacePath, string? cursor, CancellationToken ct);
    Task<CodexThread> GetAsync(string threadId, CancellationToken ct);
    Task<CodexThreadDetail> DetailAsync(string threadId, CancellationToken ct);
    Task<QueueOutcome> QueueAsync(string threadId, string message, CancellationToken ct);
}

public sealed class CodexConnectionException(string message, int? rpcCode = null) : Exception(message)
{
    public int? RpcCode { get; } = rpcCode;
}
