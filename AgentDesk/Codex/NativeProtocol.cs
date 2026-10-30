using System.Text.Json.Serialization;
using System.Text.Json;
using JetBrains.Annotations;

namespace AgentDesk.Codex;

// Internal wire contracts. Public inbox responses stay independent of the native protocol.
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeThreadList(NativeThread[] Data, string? NextCursor);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeThreadRead(NativeThread Thread);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeGoalResponse(NativeGoal? Goal);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeGoal(string? Objective, string? Status);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeTurnPage(NativeTurn[] Data);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeTurn(string? Id, string? Status, NativeItem[] Items);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeItem(string? Type, string? Text);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeStatus(string? Type, string[]? ActiveFlags);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeMcpServerPage([property: JsonRequired] NativeMcpServer[] Data, string? NextCursor);
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeMcpServer([property: JsonRequired] string Name,
    [property: JsonRequired] string AuthStatus, string? RuntimeStatus,
    [property: JsonRequired] Dictionary<string, JsonElement> Tools, string? ToolsError,
    string? HttpOrigin, string? PluginId);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
internal sealed record NativeThread
{
    public required string Id
    {
        get; init;
    }
    public string? SessionId
    {
        get; init;
    }
    public string? Name
    {
        get; init;
    }
    public string? Preview
    {
        get; init;
    }

    [JsonPropertyName("cwd")]
    public string? WorkspacePath
    {
        get; init;
    }

    public required NativeStatus Status
    {
        get; init;
    }
    public string? Model
    {
        get; init;
    }
    public string? ReasoningEffort
    {
        get; init;
    }
    public string? ParentThreadId
    {
        get; init;
    }
    public string? ForkedFromId
    {
        get; init;
    }

    [JsonPropertyName("path")]
    public string? TranscriptPath
    {
        get; init;
    }

    public string? Originator
    {
        get; init;
    }
    public string? AgentRole
    {
        get; init;
    }
    public long UpdatedAt
    {
        get; init;
    }

    public CodexThread ToResponse()
    {
        var preview = Preview ?? string.Empty;
        return new CodexThread(
            Id,
            SessionId ?? Id,
            Name ?? preview[..Math.Min(preview.Length, 90)],
            WorkspacePath ?? string.Empty,
            Status.Type ?? "unknown",
            Status.ActiveFlags ?? [],
            Model,
            ReasoningEffort,
            ParentThreadId,
            ForkedFromId,
            TranscriptPath,
            Originator,
            AgentRole,
            DateTimeOffset.FromUnixTimeSeconds(UpdatedAt));
    }
}
