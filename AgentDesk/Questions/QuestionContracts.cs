using JetBrains.Annotations;
using AgentDesk.Domain;
using System.Text.Json.Serialization;

namespace AgentDesk.Questions;

/// <summary>An agent-proposed choice with an explicitly estimated impact and optional continuation prompt.</summary>
public sealed record QuestionChoice(string Label, string? Impact = null, string? FollowUpPrompt = null);

/// <summary>Identity verified against the native Codex server at question creation time.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record QuestionOrigin(string ThreadId, string SessionId, string WorkspacePath,
    string ThreadName, string? ParentThreadId, string? ForkedFromId, string? TranscriptPath, DateTimeOffset VerifiedAt);

/// <summary>A durable question. This is a discussion item, not permission to execute a command.</summary>
public sealed record QuestionResponse(
    string Id, string Prompt, QuestionChoice[] Choices, bool AllowFreeText, string? Context,
    string Project, string? ConversationId, string Source, bool Blocking,
    QuestionState State, DateTimeOffset CreatedAt, int Version,
    string? RequestKey, string? Goal = null,
    string? FollowUpPrompt = null, QuestionOrigin? Origin = null);

/// <summary>Input for a new question. A request key makes retries safe.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateQuestionRequest(
    string Prompt, QuestionChoice[]? Choices = null, bool AllowFreeText = true, string? Context = null,
    string Project = "Allgemein", string? ConversationId = null, string Source = "Agent",
    bool Blocking = false, string? RequestKey = null,
    string? Goal = null, string? FollowUpPrompt = null, string? OriginThreadId = null);

/// <summary>A human answer or dismissal, guarded by the version displayed in the UI.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnswerQuestionRequest(int ExpectedVersion, QuestionDecision Decision);

/// <summary>A consistent inbox snapshot and its durable change cursor.</summary>
public sealed record QuestionSnapshot(long Revision, IReadOnlyList<QuestionResponse> Questions);

/// <summary>Arguments for listing the inbox.</summary>
public sealed record QuestionFilter(string? Project = null, string? ConversationId = null, string? Status = null);

public interface IQuestionService
{
    Task<QuestionResponse> CreateAsync(CreateQuestionRequest request, CancellationToken cancellationToken);
    Task<QuestionSnapshot> ListAsync(QuestionFilter filter, CancellationToken cancellationToken);
    Task<QuestionResponse> GetAsync(string id, CancellationToken cancellationToken);
    Task<QuestionResponse> AnswerAsync(string id, AnswerQuestionRequest request, CancellationToken cancellationToken);
}

public sealed class QuestionException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
