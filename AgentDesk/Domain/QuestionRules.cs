using AgentDesk.Questions;

namespace AgentDesk.Domain;

/// <summary>Pure question decisions. Time is an explicit input; no I/O or ambient state.</summary>
public static class QuestionRules
{
    public static DomainResult<CreateQuestionRequest> Prepare(CreateQuestionRequest request)
    {
        var choices = request.Choices ?? [];
        if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 4000)
            return new InvalidInput("Die Frage muss 1 bis 4000 Zeichen enthalten.");
        if (choices.Length > 3 || choices.Any(IsInvalidChoice))
            return new InvalidInput("Maximal drei gültige Vorschläge sind erlaubt.");
        if (choices.Select(choice => choice.Label.Trim()).Distinct(StringComparer.Ordinal).Count() != choices.Length)
            return new InvalidInput("Antwortvorschläge müssen unterschiedlich sein.");
        if (!request.AllowFreeText && choices.Length == 0)
            return new InvalidInput("Ohne Freitext ist mindestens ein Vorschlag erforderlich.");
        if (string.IsNullOrWhiteSpace(request.Project) || request.Project.Length > 200
            || string.IsNullOrWhiteSpace(request.Source) || request.Source.Length > 100
            || request.Context?.Length > 16000 || request.ConversationId?.Length > 200
            || request.RequestKey?.Length > 200 || request.Goal?.Length > 4000 || request.FollowUpPrompt?.Length > 4000
            || request.RequestKey is not null && string.IsNullOrWhiteSpace(request.RequestKey))
            return new InvalidInput("Projekt, Quelle oder Kontext haben ein ungültiges Format.");

        return new Succeeded<CreateQuestionRequest>(request with
        {
            Prompt = request.Prompt.Trim(),
            Project = request.Project.Trim(),
            Source = request.Source.Trim(),
            Choices = choices.Select(choice => choice with { Label = choice.Label.Trim() }).ToArray()
        });
    }

    public static DomainResult<QuestionResponse> Decide(QuestionResponse question, AnswerQuestionRequest request, DateTimeOffset now)
    {
        DomainResult<QuestionResponse> available = question.Version != request.ExpectedVersion
            ? new StateConflict("Diese Frage wurde inzwischen geändert. Bitte aktualisieren.")
            : question.State switch
            {
                PendingQuestion => new Succeeded<QuestionResponse>(question),
                AnsweredQuestion => new StateConflict("Diese Frage ist bereits beantwortet."),
                DismissedQuestion => new StateConflict("Diese Frage wurde zurückgezogen."),
                null => throw new InvalidOperationException("Question state is not initialized.")
            };

        return available.Bind(current => Apply(current, request.Decision, now)
            .Map(state => current with { State = state, Version = current.Version + 1 }));
    }

    private static DomainResult<QuestionState> Apply(QuestionResponse question, QuestionDecision decision, DateTimeOffset now) => decision switch
    {
        AnswerQuestion answer => Answer(question, answer.Text, now),
        DismissQuestion => new Succeeded<QuestionState>(new DismissedQuestion(now)),
        null => new InvalidInput("Bitte eine Antwort oder das Zurückziehen angeben.")
    };

    private static DomainResult<QuestionState> Answer(QuestionResponse question, string? text, DateTimeOffset now)
    {
        var answer = text?.Trim();
        if (string.IsNullOrWhiteSpace(answer) || answer.Length > 8000)
            return new InvalidInput("Die Antwort muss 1 bis 8000 Zeichen enthalten.");
        if (!question.AllowFreeText && question.Choices.All(choice => choice.Label != answer))
            return new InvalidInput("Bitte einen der angebotenen Vorschläge wählen.");
        return new Succeeded<QuestionState>(new AnsweredQuestion(answer, now));
    }

    // JSON input may contain null elements despite the collection's C# annotation.
    private static bool IsInvalidChoice(QuestionChoice? choice) => choice is null
        || string.IsNullOrWhiteSpace(choice.Label) || choice.Label.Length > 300
        || choice.Impact?.Length > 2000 || choice.FollowUpPrompt?.Length > 4000;
}
