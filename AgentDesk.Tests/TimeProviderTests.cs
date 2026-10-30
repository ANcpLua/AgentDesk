using Microsoft.Extensions.Time.Testing;
using AgentDesk.Codex;
using AgentDesk.Domain;
using AgentDesk.Questions;
using AgentDesk.Workspace;

namespace AgentDesk.Tests;

[TestClass]
public sealed class TimeProviderTests(TestContext context)
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task QuestionTimestampsFollowTheInjectedClock()
    {
        var clock = new FakeTimeProvider(Start);
        using var service = new QuestionService(":memory:", new NoCodex(), clock);
        var question = await service.CreateAsync(new("Question?"), context.CancellationToken);
        Assert.AreEqual(Start, question.CreatedAt);

        clock.Advance(TimeSpan.FromMinutes(7));
        var answered = await service.AnswerAsync(question.Id, new(1, new AnswerQuestion("Yes")), context.CancellationToken);
        Assert.AreEqual(new AnsweredQuestion("Yes", Start.AddMinutes(7)), answered.State);
        Assert.AreEqual(Start, answered.CreatedAt);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task WorkTimestampsAdvanceWithoutSleeping()
    {
        var clock = new FakeTimeProvider(Start);
        using var questions = new QuestionService(":memory:", new NoCodex(), clock);
        using var workspace = new WorkspaceService(":memory:", new NoCodex(), questions, clock);
        var item = await workspace.CreateWorkItemAsync(new("Verify", "/project", new OperatorOwner()), context.CancellationToken);
        Assert.AreEqual(Start, item.CreatedAt);
        Assert.AreEqual(Start, item.UpdatedAt);
        clock.Advance(TimeSpan.FromHours(2));
        var completed = await workspace.UpdateWorkItemAsync(item.Id, new(1, new CompletedWork("Checked")), context.CancellationToken);
        Assert.AreEqual(Start, completed.CreatedAt);
        Assert.AreEqual(Start.AddHours(2), completed.UpdatedAt);
        Assert.AreEqual(new CompletedWork("Checked"), completed.State);
        Assert.AreEqual(2, completed.Version);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task OriginAndCreationKeepTheirSeparateClockReads()
    {
        var clock = new FakeTimeProvider(Start) { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };
        using var service = new QuestionService(":memory:", new StubCodex(), clock);
        var question = await service.CreateAsync(new("Question?", OriginThreadId: "11111111-1111-4111-8111-111111111111"), context.CancellationToken);
        Assert.IsNotNull(question.Origin);
        Assert.AreEqual("11111111-1111-4111-8111-111111111111", question.Origin.ThreadId);
        Assert.AreEqual(Start, question.Origin.VerifiedAt);
        Assert.AreEqual(Start.AddSeconds(1), question.CreatedAt);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task InboxStreamCanAdvanceAndCancelWithoutRealDelay()
    {
        var clock = new FakeTimeProvider(Start);
        using var questions = new QuestionService(":memory:", new NoCodex(), clock);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        await using var stream = QuestionEndpoints.StreamSnapshots(questions, clock, cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await stream.MoveNextAsync());
        Assert.IsEmpty(stream.Current.Data.Questions);
        var firstRevision = stream.Current.Data.Revision;
        var next = stream.MoveNextAsync().AsTask();
        Assert.IsFalse(next.IsCompleted);
        var question = await questions.CreateAsync(new("One new question"), cancellation.Token);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(await next);
        Assert.IsGreaterThan(firstRevision, stream.Current.Data.Revision);
        var emitted = Assert.ContainsSingle(stream.Current.Data.Questions);
        Assert.AreEqual(question.Id, emitted.Id);
        Assert.AreEqual("One new question", emitted.Prompt);
        Assert.AreEqual(new PendingQuestion(), emitted.State);
        Assert.AreEqual(1, emitted.Version);
        Assert.AreEqual(Start, emitted.CreatedAt);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    private sealed class StubCodex : ICodexClient
    {
        public Task<CodexHooks> HooksAsync(string? workspacePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexTokenActivity> TokenActivityAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexMcpServerPage> McpServersAsync(string? threadId, string? serverName, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexUsage> UsageAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexThreadPage> ListAsync(string? workspacePath, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexThread> GetAsync(string threadId, CancellationToken ct) => Task.FromResult(new CodexThread(
            threadId, threadId, "Test agent", "/project", "idle", [], null, null, null, null, null, "test", null, Start));
        public Task<CodexThreadDetail> DetailAsync(string threadId, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueOutcome> QueueAsync(string threadId, string message, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoCodex : ICodexClient
    {
        public Task<CodexHooks> HooksAsync(string? workspacePath, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexTokenActivity> TokenActivityAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexMcpServerPage> McpServersAsync(string? threadId, string? serverName, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexUsage> UsageAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexThreadPage> ListAsync(string? workspacePath, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexThread> GetAsync(string threadId, CancellationToken ct) => throw new NotSupportedException();
        public Task<CodexThreadDetail> DetailAsync(string threadId, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueueOutcome> QueueAsync(string threadId, string message, CancellationToken ct) => throw new NotSupportedException();
    }
}
