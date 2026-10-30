using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace AgentDesk.Codex;

/// <summary>A read-only snapshot from hooks/list. Native enum values remain open strings.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexHooks([property: JsonRequired] CodexHooksEntry[] Data);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexHooksEntry([property: JsonRequired] string Cwd,
    [property: JsonRequired] CodexHook[] Hooks, [property: JsonRequired] string[] Warnings,
    [property: JsonRequired] CodexHookError[] Errors);

[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexHookError([property: JsonRequired] string Path, [property: JsonRequired] string Message);

/// <summary>Native trust status describes Codex's decision, independently of enabled state.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record CodexHook(
    [property: JsonRequired] string Key, [property: JsonRequired] string EventName, string? Matcher,
    [property: JsonRequired] long TimeoutSec, string? StatusMessage, double? AdditionalContextLimit,
    [property: JsonRequired] string SourcePath, [property: JsonRequired] string Source, string? PluginId,
    [property: JsonRequired] long DisplayOrder, [property: JsonRequired] bool Enabled,
    [property: JsonRequired] bool IsManaged, [property: JsonRequired] string CurrentHash,
    [property: JsonRequired] string TrustStatus, [property: JsonRequired] string HandlerType,
    string? Command, bool? Async, string? Server, string? Tool);
