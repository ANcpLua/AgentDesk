using AgentDesk.Infrastructure;

namespace AgentDesk.Questions;

// The stdio bridge uses the same HTTP service as the browser. It never owns a second database.
public sealed class HttpQuestionService(InboxHttpTransport transport) : IQuestionService
{
    public Task<QuestionResponse> CreateAsync(CreateQuestionRequest request, CancellationToken ct) =>
        SendAsync<QuestionResponse>(HttpMethod.Post, "/api/questions/", request, ct);
    public Task<QuestionResponse> GetAsync(string id, CancellationToken ct) =>
        SendAsync<QuestionResponse>(HttpMethod.Get, $"/api/questions/{Uri.EscapeDataString(id)}", null, ct);
    public Task<QuestionResponse> AnswerAsync(string id, AnswerQuestionRequest request, CancellationToken ct) =>
        SendAsync<QuestionResponse>(HttpMethod.Post, $"/api/questions/{Uri.EscapeDataString(id)}/answer", request, ct);
    public Task<QuestionSnapshot> ListAsync(QuestionFilter filter, CancellationToken ct)
    {
        var query = new Dictionary<string, string?>
        {
            ["project"] = filter.Project,
            ["conversationId"] = filter.ConversationId,
            ["status"] = filter.Status
        };
        var parameters = string.Join("&", query.Where(x => x.Value is not null)
            .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}"));
        return SendAsync<QuestionSnapshot>(HttpMethod.Get, "/api/questions/?" + parameters, null, ct);
    }

    private Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        transport.SendAsync<T>(method, path, body, ct, "Der Fragen-Dienst ist nicht erreichbar.");
}
