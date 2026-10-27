using AgentDesk.Workspace;

namespace AgentDesk.Domain;

public static class WorkRules
{
    public static DomainResult<WorkOwner> ValidateOwner(WorkOwner owner) => owner switch
    {
        OperatorOwner human => new Succeeded<WorkOwner>(human),
        AgentOwner agent => Guid.TryParse(agent.ThreadId, out _)
            ? new Succeeded<WorkOwner>(agent)
            : new InvalidInput("Eine gültige Agent-Thread-ID ist erforderlich."),
        null => new InvalidInput("Eine Zuständigkeit ist erforderlich.")
    };

    public static DomainResult<WorkState> Validate(WorkState state) => state switch
    {
        TodoWork todo => ValidateNote(todo.Note).Map<string?, WorkState>(_ => todo),
        InProgressWork progress => ValidateNote(progress.Note).Map<string?, WorkState>(_ => progress),
        BlockedWork blocked => RequiredText(blocked.Reason, "Bitte das Hindernis beschreiben.").Map<string, WorkState>(_ => blocked),
        CompletedWork done => RequiredText(done.Evidence, "Zum Abschließen bitte das Ergebnis festhalten.").Map<string, WorkState>(_ => done),
        null => new InvalidInput("Ein Aufgabenstatus ist erforderlich.")
    };

    public static DomainResult<WorkItem> Update(WorkItem item, UpdateWorkItemRequest request, DateTimeOffset now) =>
        item.Version != request.ExpectedVersion
            ? new StateConflict("Die Aufgabe wurde inzwischen geändert. Bitte aktualisieren.")
            : Validate(request.State).Map(state => item with { State = state, UpdatedAt = now, Version = item.Version + 1 });

    private static DomainResult<string?> ValidateNote(string? note) => note?.Length > 8000
        ? new InvalidInput("Die Notiz darf höchstens 8000 Zeichen enthalten.")
        : new Succeeded<string?>(note);

    private static DomainResult<string> RequiredText(string? text, string missingMessage) =>
        string.IsNullOrWhiteSpace(text) ? new InvalidInput(missingMessage)
        : text.Length > 8000 ? new InvalidInput("Das Ergebnis darf höchstens 8000 Zeichen enthalten.")
        : new Succeeded<string>(text);
}
