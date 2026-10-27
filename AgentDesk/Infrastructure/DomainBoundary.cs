using AgentDesk.Domain;
using AgentDesk.Questions;

namespace AgentDesk.Infrastructure;

/// <summary>Translate pure domain outcomes at the existing HTTP/MCP exception boundary.</summary>
public static class DomainBoundary
{
    public static T RequireSuccess<T>(this DomainResult<T> result) => result switch
    {
        Succeeded<T> success => success.Value,
        InvalidInput invalid => throw new QuestionException(400, invalid.Message),
        StateConflict conflict => throw new QuestionException(409, conflict.Message),
        null => throw new InvalidOperationException("Domain result is not initialized.")
    };
}
