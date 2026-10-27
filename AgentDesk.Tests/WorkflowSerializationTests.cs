using System.Text.Json;
using AgentDesk.Domain;

namespace AgentDesk.Tests;

[TestClass]
public sealed class WorkflowSerializationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DynamicData(nameof(QuestionCases))]
    public void QuestionStatesUseExpectedWireContract(QuestionState state, string expectedJson) =>
        AssertWireContract(state, expectedJson);

    public static IEnumerable<TestDataRow<(QuestionState, string)>> QuestionCases =>
    [
        new((new PendingQuestion(), """{"kind":"pending"}""")) { DisplayName = "Question_Pending" },
        new((new AnsweredQuestion("yes", Now), """{"kind":"answered","text":"yes","at":"2026-10-27T12:00:00+00:00"}""")) { DisplayName = "Question_Answered" },
        new((new DismissedQuestion(Now), """{"kind":"dismissed","at":"2026-10-27T12:00:00+00:00"}""")) { DisplayName = "Question_Dismissed" }
    ];

    [TestMethod]
    [DynamicData(nameof(DeliveryCases))]
    public void DeliveryStatesUseExpectedWireContract(DeliveryState state, string expectedJson) =>
        AssertWireContract(state, expectedJson);

    public static IEnumerable<TestDataRow<(DeliveryState, string)>> DeliveryCases =>
    [
        new((new SendingDelivery(), """{"kind":"sending"}""")) { DisplayName = "Delivery_Sending" },
        new((new QueuedDelivery("m1"), """{"kind":"queued","messageId":"m1"}""")) { DisplayName = "Delivery_Queued" },
        new((new FailedDelivery("no"), """{"kind":"failed","reason":"no"}""")) { DisplayName = "Delivery_Failed" },
        new((new UncertainDelivery("maybe"), """{"kind":"uncertain","reason":"maybe"}""")) { DisplayName = "Delivery_Uncertain" }
    ];

    [TestMethod]
    [DynamicData(nameof(OwnerCases))]
    public void OwnersUseExpectedWireContract(WorkOwner owner, string expectedJson) =>
        AssertWireContract(owner, expectedJson);

    public static IEnumerable<TestDataRow<(WorkOwner, string)>> OwnerCases =>
    [
        new((new OperatorOwner(), """{"kind":"operator"}""")) { DisplayName = "Owner_Operator" },
        new((new AgentOwner("thread-1"), """{"kind":"agent","threadId":"thread-1"}""")) { DisplayName = "Owner_Agent" }
    ];

    [TestMethod]
    [DynamicData(nameof(WorkCases))]
    public void WorkStatesUseExpectedWireContract(WorkState state, string expectedJson) =>
        AssertWireContract(state, expectedJson);

    public static IEnumerable<TestDataRow<(WorkState, string)>> WorkCases =>
    [
        new((new TodoWork(), """{"kind":"todo","note":null}""")) { DisplayName = "Work_Todo" },
        new((new InProgressWork("running"), """{"kind":"in_progress","note":"running"}""")) { DisplayName = "Work_InProgress" },
        new((new BlockedWork("input missing"), """{"kind":"blocked","reason":"input missing"}""")) { DisplayName = "Work_Blocked" },
        new((new CompletedWork("done"), """{"kind":"done","evidence":"done"}""")) { DisplayName = "Work_Completed" }
    ];

    [TestMethod]
    public void UnknownQuestionDiscriminatorIsRejected() =>
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<QuestionState>("""{"kind":"unknown"}""", Json));

    [TestMethod]
    public void MissingQuestionDiscriminatorIsRejectedByDefaultSerializer() =>
        Assert.ThrowsExactly<NotSupportedException>(() => JsonSerializer.Deserialize<QuestionState>("{}", Json));

    [TestMethod]
    public void UnknownOwnerDiscriminatorIsRejected() =>
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<WorkOwner>("""{"kind":"robot"}""", Json));

    [TestMethod]
    public void ForwardedAnswerCannotOverrideSourceThread() =>
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<DispatchContent>(
            """{"kind":"answer","questionId":"q","sourceThreadId":"other"}""", Json));

    [TestMethod]
    public void DismissalCannotIncludeAnswerText() =>
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<QuestionDecision>(
            """{"kind":"dismiss","text":"yes"}""", Json));

    [TestMethod]
    public void AgentOwnerRequiresThreadId() =>
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<WorkOwner>("""{"kind":"agent"}""", Json));

    private static void AssertWireContract<T>(T value, string expectedJson)
    {
        var actualJson = JsonSerializer.Serialize(value, Json);
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        Assert.IsTrue(JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
            $"Expected JSON: {expectedJson}; actual JSON: {actualJson}");
        Assert.AreEqual(value, JsonSerializer.Deserialize<T>(expectedJson, Json));
    }
}
