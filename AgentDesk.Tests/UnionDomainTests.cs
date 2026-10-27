using AgentDesk.Domain;
using AgentDesk.Infrastructure;
using AgentDesk.Questions;
using AgentDesk.Workspace;

namespace AgentDesk.Tests;

[TestClass]
public sealed class UnionDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void QueueOutcomesMapToDistinctDeliveryStates()
    {
        QueueOutcome accepted = new QueueAccepted("message-1");
        QueueOutcome rejected = new QueueRejected("not accepted");
        QueueOutcome uncertain = new QueueUncertain("receipt missing");

        Assert.AreEqual(new QueuedDelivery("message-1"), accepted.ToDeliveryState());
        Assert.AreEqual(new FailedDelivery("not accepted"), rejected.ToDeliveryState());
        Assert.AreEqual(new UncertainDelivery("receipt missing"), uncertain.ToDeliveryState());
    }

    [TestMethod]
    public void DefaultUnionsAreRejectedInsteadOfBecomingSuccessfulStates()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => default(QueueOutcome).ToDeliveryState());
        Assert.ThrowsExactly<InvalidOperationException>(() => default(DomainResult<int>).RequireSuccess());
    }

    [TestMethod]
    public void RecoveryOnlyChangesInFlightDelivery()
    {
        var queued = new QueuedDelivery("accepted-before-restart");
        var failed = new FailedDelivery("rejected");
        var uncertain = new UncertainDelivery("unknown");

        Assert.AreEqual(new UncertainDelivery("Dienst wurde während der Zustellung beendet. Ziel-Thread vor erneutem Senden prüfen."),
            new SendingDelivery().Recover());
        Assert.AreSame(queued, queued.Recover());
        Assert.AreSame(failed, failed.Recover());
        Assert.AreSame(uncertain, uncertain.Recover());
    }

    [TestMethod]
    public void QuestionDecisionProducesNewAnsweredValueWithoutMutatingInput()
    {
        var original = Pending();
        var result = QuestionRules.Decide(original, new(1, new AnswerQuestion("  Option A  ")), Now).RequireSuccess();

        Assert.AreEqual(new AnsweredQuestion("Option A", Now), result.State);
        Assert.AreEqual(2, result.Version);
        Assert.AreEqual(original.Id, result.Id);
        Assert.AreEqual(original.CreatedAt, result.CreatedAt);
        Assert.IsInstanceOfType<PendingQuestion>(original.State);
        Assert.AreEqual(1, original.Version);
    }

    [TestMethod]
    public void DismissalHasItsOwnCaseWithoutAnAnswerPayload()
    {
        var result = QuestionRules.Decide(Pending(), new(1, new DismissQuestion()), Now).RequireSuccess();
        Assert.AreEqual(new DismissedQuestion(Now), result.State);
        Assert.AreEqual(2, result.Version);
    }

    [TestMethod]
    public void UnknownChoiceReturnsInvalidInput()
    {
        var result = QuestionRules.Decide(Pending() with { AllowFreeText = false },
            new(1, new AnswerQuestion("unknown")), Now);

        var failure = Assert.IsInstanceOfType<InvalidInput>(result.Value);
        Assert.AreEqual("Bitte einen der angebotenen Vorschläge wählen.", failure.Message);
    }

    [TestMethod]
    public void AnsweredQuestionCannotBeDismissed()
    {
        var result = QuestionRules.Decide(Pending() with { State = new AnsweredQuestion("Option A", Now) },
            new(1, new DismissQuestion()), Now);

        var failure = Assert.IsInstanceOfType<StateConflict>(result.Value);
        Assert.AreEqual("Diese Frage ist bereits beantwortet.", failure.Message);
    }

    [TestMethod]
    [DataRow(null, DisplayName = "EmptyAnswer_Null")]
    [DataRow("", DisplayName = "EmptyAnswer_Empty")]
    [DataRow("  ", DisplayName = "EmptyAnswer_Whitespace")]
    public void EmptyAnswerReturnsInvalidInput(string? answer)
    {
        var result = QuestionRules.Decide(Pending(), new(1, new AnswerQuestion(answer!)), Now);

        var failure = Assert.IsInstanceOfType<InvalidInput>(result.Value);
        Assert.AreEqual("Die Antwort muss 1 bis 8000 Zeichen enthalten.", failure.Message);
    }

    [TestMethod]
    public void StaleQuestionVersionReturnsConflict()
    {
        var result = QuestionRules.Decide(Pending(), new(2, new AnswerQuestion("Option A")), Now);

        var failure = Assert.IsInstanceOfType<StateConflict>(result.Value);
        Assert.AreEqual("Diese Frage wurde inzwischen geändert. Bitte aktualisieren.", failure.Message);
    }

    [TestMethod]
    public void MissingDecisionReturnsInvalidInput()
    {
        var result = QuestionRules.Decide(Pending(), new(1, null!), Now);

        var failure = Assert.IsInstanceOfType<InvalidInput>(result.Value);
        Assert.AreEqual("Bitte eine Antwort oder das Zurückziehen angeben.", failure.Message);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "Composition_InvalidInput")]
    [DataRow(true, DisplayName = "Composition_StateConflict")]
    public void MapAndBindSkipSuccessCallbacksAndPreserveFailureIdentity(bool conflict)
    {
        DomainResult<int> result = conflict
            ? new StateConflict("conflict")
            : new InvalidInput("invalid input");
        var failure = result.Value;

        var mapCalls = 0;
        var bindCalls = 0;
        var mapped = result.Map(value => { mapCalls++; return value + 1; });
        var bound = result.Bind<int, string>(value => { bindCalls++; return new Succeeded<string>(value.ToString()); });

        Assert.AreEqual(0, mapCalls, "Map must skip its callback for a failure.");
        Assert.AreEqual(0, bindCalls, "Bind must skip its callback for a failure.");
        Assert.AreSame(failure, mapped.Value);
        Assert.AreSame(failure, bound.Value);
    }

    [TestMethod]
    public void SuccessfulCompositionKeepsThePayloadTyped()
    {
        DomainResult<int> start = new Succeeded<int>(3);
        var result = start.Map(value => value * 2)
            .Bind<int, string>(value => new Succeeded<string>($"value={value}"));
        Assert.AreEqual("value=6", result.RequireSuccess());
    }

    [TestMethod]
    public void CallbackExceptionsAreNotTurnedIntoDomainFailures()
    {
        DomainResult<int> start = new Succeeded<int>(3);
        var expected = new ArithmeticException("callback bug");
        var actual = Assert.ThrowsExactly<ArithmeticException>(() => start.Map<int, int>(_ => throw expected));
        Assert.AreSame(expected, actual);
    }

    [TestMethod]
    public void WorkCompletionRetainsOperatorOwnershipAndOriginalState()
    {
        var item = new WorkItem("w1", "Verify", null, "/project", new OperatorOwner(), null, new TodoWork(), Now, Now, 1);
        var later = Now.AddMinutes(1);
        var completed = WorkRules.Update(item, new(1, new CompletedWork("Verified result")), later).RequireSuccess();
        Assert.AreEqual(new CompletedWork("Verified result"), completed.State);
        Assert.AreSame(item.Owner, completed.Owner);
        Assert.AreEqual(item.CreatedAt, completed.CreatedAt);
        Assert.AreEqual(later, completed.UpdatedAt);
        Assert.AreEqual(Now, item.UpdatedAt);
        Assert.AreEqual(2, completed.Version);
        Assert.AreEqual(1, item.Version);
        Assert.IsInstanceOfType<TodoWork>(item.State);
    }

    [TestMethod]
    public void BlockedWorkRequiresAReason()
    {
        var failure = Assert.IsInstanceOfType<InvalidInput>(WorkRules.Validate(new BlockedWork("  ")).Value);
        Assert.AreEqual("Bitte das Hindernis beschreiben.", failure.Message);
        Assert.AreEqual(new BlockedWork("Waiting for operator"), WorkRules.Validate(new BlockedWork("Waiting for operator")).RequireSuccess());
    }

    [TestMethod]
    public void WorkCompletionWithoutEvidenceReturnsInvalidInput()
    {
        var item = new WorkItem("w1", "Verify", null, "/project", new OperatorOwner(), null, new TodoWork(), Now, Now, 1);

        var result = WorkRules.Update(item, new(1, new CompletedWork("")), Now);

        var failure = Assert.IsInstanceOfType<InvalidInput>(result.Value);
        Assert.AreEqual("Zum Abschließen bitte das Ergebnis festhalten.", failure.Message);
    }

    [TestMethod]
    [DataRow(null, DisplayName = "AgentOwner_NullId")]
    [DataRow("display name", DisplayName = "AgentOwner_DisplayName")]
    public void InvalidAgentIdentityCannotBecomeAnOwner(string? threadId)
    {
        var result = WorkRules.ValidateOwner(new AgentOwner(threadId!));

        var failure = Assert.IsInstanceOfType<InvalidInput>(result.Value);
        Assert.AreEqual("Eine gültige Agent-Thread-ID ist erforderlich.", failure.Message);
    }

    [TestMethod]
    public void MissingOwnerReturnsInvalidInput()
    {
        var failure = Assert.IsInstanceOfType<InvalidInput>(WorkRules.ValidateOwner(null!).Value);
        Assert.AreEqual("Eine Zuständigkeit ist erforderlich.", failure.Message);
    }

    [TestMethod]
    public void OperatorOwnerIsPreserved()
    {
        var owner = new OperatorOwner();
        Assert.AreSame(owner, WorkRules.ValidateOwner(owner).RequireSuccess());
    }

    private static QuestionResponse Pending() => new("q1", "Choose", [new("Option A"), new("Option B")],
        true, null, "/project", "task", "agent", false, new PendingQuestion(), Now, 1, "request");
}
