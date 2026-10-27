using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using AgentDesk.Questions;
using AgentDesk.Codex;

namespace AgentDesk.Middleware;

public sealed class QuestionExceptionHandler(ILogger<QuestionExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var error = exception switch
        {
            QuestionException known => known,
            CodexConnectionException connection => new QuestionException(503, connection.Message),
            _ => null
        };
        if (error is null)
            return false;
        logger.LogInformation("Question request rejected with status {StatusCode}", error.StatusCode);
        context.Response.StatusCode = error.StatusCode;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = error.StatusCode,
            Title = error.Message,
            Instance = context.Request.Path
        }, ct);
        return true;
    }
}
