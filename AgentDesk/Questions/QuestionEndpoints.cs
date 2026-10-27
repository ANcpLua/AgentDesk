using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;

namespace AgentDesk.Questions;

public static class QuestionEndpoints
{
    public static void MapQuestions(this WebApplication app)
    {
        var api = app.MapGroup("/api/questions").WithTags("Questions");
        api.MapGet("/", async (string? project, string? conversationId, string? status,
            IQuestionService service, CancellationToken ct) =>
                TypedResults.Ok(await service.ListAsync(new(project, conversationId, status), ct)))
            .WithName("ListQuestions").WithSummary("Read the durable inbox and revision.");
        api.MapGet("/{id}", async (string id, IQuestionService service, CancellationToken ct) =>
                TypedResults.Ok(await service.GetAsync(id, ct)))
            .WithName("GetQuestion").WithSummary("Read one question and its human response.").ProducesProblem(404);
        api.MapPost("/", async (CreateQuestionRequest request, IQuestionService service, CancellationToken ct) =>
        {
            var question = await service.CreateAsync(request, ct);
            return TypedResults.Created($"/api/questions/{question.Id}", question);
        }).WithName("CreateQuestion").WithSummary("Store a question without blocking its caller.")
            .ProducesProblem(400).ProducesProblem(409);
        api.MapPost("/{id}/answer", async (string id, AnswerQuestionRequest request,
            IQuestionService service, CancellationToken ct) =>
                TypedResults.Ok(await service.AnswerAsync(id, request, ct)))
            .WithName("AnswerQuestion").WithSummary("Answer or dismiss a pending question once.")
            .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409);
        api.MapGet("/stream", (IQuestionService service, TimeProvider timeProvider, CancellationToken ct) =>
                TypedResults.ServerSentEvents(StreamSnapshots(service, timeProvider, ct)))
            .WithName("StreamQuestionSnapshots").WithSummary("Stream complete inbox snapshots, including on reconnection.");
    }

    internal static async IAsyncEnumerable<SseItem<QuestionSnapshot>> StreamSnapshots(
        IQuestionService service, TimeProvider timeProvider, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        long revision = -1;
        var idle = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), timeProvider);
        do
        {
            var snapshot = await service.ListAsync(new(), cancellationToken);
            // Complete snapshots make reconnects independent of any in-memory event buffer.
            if (snapshot.Revision != revision || ++idle >= 15)
            {
                revision = snapshot.Revision;
                idle = 0;
                yield return new SseItem<QuestionSnapshot>(snapshot, "inbox")
                {
                    EventId = revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ReconnectionInterval = TimeSpan.FromSeconds(2)
                };
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}
