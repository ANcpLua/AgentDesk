using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace AgentDesk.Domain;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonDerivedType(typeof(PendingQuestion), "pending")]
[JsonDerivedType(typeof(AnsweredQuestion), "answered")]
[JsonDerivedType(typeof(DismissedQuestion), "dismissed")]
public closed record QuestionState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PendingQuestion : QuestionState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record AnsweredQuestion([property: JsonRequired] string Text, [property: JsonRequired] DateTimeOffset At) : QuestionState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record DismissedQuestion([property: JsonRequired] DateTimeOffset At) : QuestionState;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonDerivedType(typeof(AnswerQuestion), "answer")]
[JsonDerivedType(typeof(DismissQuestion), "dismiss")]
public closed record QuestionDecision;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AnswerQuestion([property: JsonRequired] string Text) : QuestionDecision;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DismissQuestion : QuestionDecision;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonDerivedType(typeof(SendingDelivery), "sending")]
[JsonDerivedType(typeof(QueuedDelivery), "queued")]
[JsonDerivedType(typeof(FailedDelivery), "failed")]
[JsonDerivedType(typeof(UncertainDelivery), "uncertain")]
public closed record DeliveryState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SendingDelivery : DeliveryState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record QueuedDelivery([property: JsonRequired] string MessageId) : DeliveryState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record FailedDelivery([property: JsonRequired] string Reason) : DeliveryState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed record UncertainDelivery([property: JsonRequired] string Reason) : DeliveryState;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonDerivedType(typeof(ComposeMessage), "message")]
[JsonDerivedType(typeof(ForwardAnswer), "answer")]
public closed record DispatchContent;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComposeMessage([property: JsonRequired] string Text, string? SourceThreadId = null) : DispatchContent;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ForwardAnswer([property: JsonRequired] string QuestionId, string? Note = null) : DispatchContent;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonDerivedType(typeof(OperatorOwner), "operator")]
[JsonDerivedType(typeof(AgentOwner), "agent")]
public closed record WorkOwner;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OperatorOwner : WorkOwner;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentOwner([property: JsonRequired] string ThreadId) : WorkOwner;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonDerivedType(typeof(TodoWork), "todo")]
[JsonDerivedType(typeof(InProgressWork), "in_progress")]
[JsonDerivedType(typeof(BlockedWork), "blocked")]
[JsonDerivedType(typeof(CompletedWork), "done")]
public closed record WorkState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TodoWork(string? Note = null) : WorkState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InProgressWork(string? Note = null) : WorkState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BlockedWork([property: JsonRequired] string Reason) : WorkState;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompletedWork([property: JsonRequired] string Evidence) : WorkState;

public static class WorkflowStates
{
    public static string Kind(this QuestionState state) => state switch
    {
        PendingQuestion => "pending",
        AnsweredQuestion => "answered",
        DismissedQuestion => "dismissed",
        null => throw new InvalidOperationException("Question state is not initialized.")
    };

    public static DeliveryState Recover(this DeliveryState state) => state switch
    {
        SendingDelivery => new UncertainDelivery("Dienst wurde während der Zustellung beendet. Ziel-Thread vor erneutem Senden prüfen."),
        QueuedDelivery queued => queued,
        FailedDelivery failed => failed,
        UncertainDelivery uncertain => uncertain,
        null => throw new InvalidOperationException("Delivery state is not initialized.")
    };

    public static DeliveryState ToDeliveryState(this QueueOutcome outcome) => outcome switch
    {
        QueueAccepted accepted => new QueuedDelivery(accepted.MessageId),
        QueueRejected rejected => new FailedDelivery(rejected.Reason),
        QueueUncertain uncertain => new UncertainDelivery(uncertain.Reason),
        null => throw new InvalidOperationException("Queue outcome is not initialized.")
    };
}
