namespace AgentDesk.Domain;

public sealed record QueueAccepted(string MessageId);
public sealed record QueueRejected(string Reason);
public sealed record QueueUncertain(string Reason);

/// <summary>Expected native queue outcomes. No exception/Boolean combination can hide the case.</summary>
public readonly union QueueOutcome(QueueAccepted, QueueRejected, QueueUncertain);
