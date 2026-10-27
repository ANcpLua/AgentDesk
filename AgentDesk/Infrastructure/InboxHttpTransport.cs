using Microsoft.AspNetCore.Mvc;
using AgentDesk.Questions;

namespace AgentDesk.Infrastructure;

/// <summary>Shared HTTP transport for the stdio MCP adapter.</summary>
public sealed class InboxHttpTransport(HttpClient client)
{
    public async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        string failureMessage = "Die Anfrage ist fehlgeschlagen.")
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Question-Inbox", "1");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken);
            throw new QuestionException((int)response.StatusCode, problem?.Title ?? failureMessage);
        }

        return (await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken))!;
    }
}
