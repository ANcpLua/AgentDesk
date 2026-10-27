using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AgentDesk.Questions;
using AgentDesk.Domain;

namespace AgentDesk.Mcp;

[McpServerToolType]
public sealed class QuestionTools(IQuestionService service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "ask_question", Title = "Frage vormerken", Destructive = false, OpenWorld = false)]
    [Description("Store a durable question for the human and return immediately. Continue independent work; do not poll or keep a turn alive solely for this question. This does not authorize actions. Use a stable requestKey when retrying. Set conversationId to an explicit task identifier to retrieve only this task's answers later.")]
    public Task<CallToolResult> AskQuestion(
        [Description("The question to ask.")] string prompt,
        [Description("Up to three choices with a label, optional estimated impact and follow-up prompt.")] QuestionChoice[]? choices = null,
        string project = "Allgemein", string? conversationId = null, string? context = null,
        bool allowFreeText = true, bool blocking = false, string source = "Agent", string? requestKey = null,
        string? goal = null, string? followUpPrompt = null,
        [Description("Your verified CODEX_THREAD_ID from this task. Never guess, use a display name or inherit another task's ID.")] string? originThreadId = null,
        CancellationToken cancellationToken = default) =>
        Execute(async () => await service.CreateAsync(new(prompt, choices, allowFreeText, context,
            project, conversationId, source, blocking, requestKey, goal, followUpPrompt, originThreadId), cancellationToken));

    [McpServerTool(Name = "list_questions", Title = "Fragen und Antworten lesen", ReadOnly = true, OpenWorld = false)]
    [Description("Read durable questions and human answers. Filter by project, conversationId and status (pending, answered, dismissed). Read relevant answers before continuing dependent work. Do not repeatedly poll in a loop.")]
    public Task<CallToolResult> ListQuestions(string? project = null, string? conversationId = null,
        string? status = null, CancellationToken cancellationToken = default) =>
        Execute(async () => await service.ListAsync(new(project, conversationId, status), cancellationToken));

    [McpServerTool(Name = "get_question", Title = "Antwort nachsehen", ReadOnly = true, OpenWorld = false)]
    [Description("Read a single durable question and its current answer by ID. Pending is not approval.")]
    public Task<CallToolResult> GetQuestion(string id, CancellationToken cancellationToken = default) =>
        Execute(async () => await service.GetAsync(id, cancellationToken));

    [McpServerTool(Name = "open_question_inbox", Title = "Fragenpostfach öffnen", ReadOnly = true, OpenWorld = false)]
    [McpMeta("ui", JsonValue = """{"resourceUri":"ui://question-inbox/view.html"}""")]
    [Description("Open the persistent questions UI. The user can answer after the agent turn has ended. In hosts without MCP Apps support, use the local inbox browser URL configured for this server.")]
    public Task<CallToolResult> OpenInbox(CancellationToken cancellationToken = default) =>
        Execute(async () => await service.ListAsync(new(), cancellationToken));

    [McpServerTool(Name = "submit_question_answer", Title = "Antwort speichern", Destructive = false, OpenWorld = false)]
    [McpMeta("ui", JsonValue = """{"visibility":["app"]}""")]
    [Description("Used by the human-facing inbox UI to save the human's chosen answer. Agents must never answer their own questions. expectedVersion prevents overwriting another response.")]
    public Task<CallToolResult> SubmitAnswer(string id, QuestionDecision decision, int expectedVersion,
        CancellationToken cancellationToken = default) =>
        Execute(async () => await service.AnswerAsync(id, new(expectedVersion, decision), cancellationToken));

    private static async Task<CallToolResult> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            var node = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = node.ToJsonString() }],
                StructuredContent = JsonSerializer.SerializeToElement(node)
            };
        }
        catch (QuestionException error)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = error.Message }],
                StructuredContent = JsonSerializer.SerializeToElement(new { error = error.Message, status = error.StatusCode })
            };
        }
    }
}
