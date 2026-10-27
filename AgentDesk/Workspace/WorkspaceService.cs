using System.Text.Json;
using Microsoft.Data.Sqlite;
using AgentDesk.Codex;
using AgentDesk.Questions;
using AgentDesk.Domain;
using AgentDesk.Infrastructure;

namespace AgentDesk.Workspace;

public sealed class WorkspaceService : IWorkspaceService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _sendSlots = new(4, 4);
    private readonly ICodexClient _codex;
    private readonly IQuestionService _questions;
    private readonly TimeProvider _timeProvider;

    public WorkspaceService(string databasePath, ICodexClient codex, IQuestionService questions, TimeProvider timeProvider)
    {
        _codex = codex;
        _questions = questions;
        _timeProvider = timeProvider;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS dispatch_batches (id TEXT PRIMARY KEY, request_key TEXT NOT NULL UNIQUE, signature TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS dispatch_deliveries (id TEXT PRIMARY KEY, batch_id TEXT NOT NULL, document TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS work_items (id TEXT PRIMARY KEY, workspace_path TEXT NOT NULL, document TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        // A crash between native queue acceptance and saving the receipt is ambiguous. Never silently resend.
        foreach (var delivery in ReadDocuments<DispatchDelivery>("dispatch_deliveries").Where(x => x.State is SendingDelivery))
            SaveDelivery(delivery with
            {
                State = delivery.State.Recover(),
                UpdatedAt = _timeProvider.GetUtcNow()
            });
    }

    public async Task<DispatchBatch> DispatchAsync(DispatchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RequestKey) || request.RequestKey.Length > 200
            || request.ThreadIds is null || request.ThreadIds.Length is < 1 or > 8
            || request.ThreadIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.ThreadIds.Length)
            throw new QuestionException(400, "Bitte 1 bis 8 unterschiedliche Threads und einen Request-Key angeben.");
        foreach (var id in request.ThreadIds)
            CodexClient.ValidateId(id);
        var signature = JsonSerializer.Serialize(request, Json);
        await _gate.WaitAsync(ct);
        try
        {
            if (FindBatch(request.RequestKey, signature) is { } existing)
                return existing;
        }
        finally
        {
            _gate.Release();
        }

        var prepared = await PrepareMessageAsync(request.Content, ct);
        var message = prepared.Text;
        if (prepared.SourceThreadId is not null)
        {
            var source = await _codex.GetAsync(prepared.SourceThreadId, ct);
            message += $"\nUrsprungs-Thread: {source.Id}\nUrsprungs-Projekt: {source.WorkspacePath}";
        }
        var recipientTasks = request.ThreadIds.Select(id => _codex.GetAsync(id, ct)).ToArray();
        var recipients = await Task.WhenAll(recipientTasks);
        var batchId = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();
        var deliveries = recipients.Select(t => new DispatchDelivery(Guid.NewGuid().ToString("N"), batchId,
            t.Id, t.Name, t.WorkspacePath, message, new SendingDelivery(), now, now)).ToArray();

        await _gate.WaitAsync(ct);
        try
        {
            // Recheck under the same lock used by reservation, so concurrent retries cannot fan out twice.
            if (ReserveBatch(request.RequestKey, signature, batchId, deliveries) is { } existing)
                return existing;
        }
        finally
        {
            _gate.Release();
        }

        // The explicit send has been durably reserved. Browser disconnects must not trigger a retry.
        var sendTasks = deliveries.Select(async delivery =>
        {
            DispatchDelivery updated;
            await _sendSlots.WaitAsync(CancellationToken.None);
            try
            {
                var outcome = await _codex.QueueAsync(delivery.ThreadId,
                    delivery.Message + $"\n\n[Postfach-Zustellung {delivery.Id}]", CancellationToken.None);
                updated = delivery with
                {
                    State = outcome.ToDeliveryState(),
                    UpdatedAt = _timeProvider.GetUtcNow()
                };
            }
            finally
            {
                _sendSlots.Release();
            }
            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                SaveDelivery(updated);
            }
            finally
            {
                _gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(sendTasks);
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            return Batch(batchId, request.RequestKey);
        }
        finally
        {
            _gate.Release();
        }
    }

    // SQLite uses synchronous I/O. These operations execute while the caller owns _gate.
    private DispatchBatch? FindBatch(string requestKey, string signature)
    {
        using var lookup = _connection.CreateCommand();
        lookup.CommandText = "SELECT id, signature FROM dispatch_batches WHERE request_key=$key";
        lookup.Parameters.AddWithValue("$key", requestKey);
        using var reader = lookup.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetString(1) != signature)
            throw new QuestionException(409, "Dieser Request-Key gehört zu einem anderen Versand.");
        var existingId = reader.GetString(0);
        reader.Close();
        return Batch(existingId, requestKey);
    }

    private DispatchBatch? ReserveBatch(string requestKey, string signature, string batchId, DispatchDelivery[] deliveries)
    {
        using var transaction = _connection.BeginTransaction();
        using var reserve = _connection.CreateCommand();
        reserve.Transaction = transaction;
        reserve.CommandText = "INSERT OR IGNORE INTO dispatch_batches(id,request_key,signature) VALUES($id,$key,$signature)";
        reserve.Parameters.AddWithValue("$id", batchId);
        reserve.Parameters.AddWithValue("$key", requestKey);
        reserve.Parameters.AddWithValue("$signature", signature);
        if (reserve.ExecuteNonQuery() == 0)
        {
            reserve.CommandText = "SELECT id, signature FROM dispatch_batches WHERE request_key=$key";
            using var reader = reserve.ExecuteReader();
            reader.Read();
            if (reader.GetString(1) != signature)
                throw new QuestionException(409, "Dieser Request-Key gehört zu einem anderen Versand.");
            var existingId = reader.GetString(0);
            reader.Close();
            transaction.Commit();
            return Batch(existingId, requestKey);
        }
        foreach (var delivery in deliveries)
            SaveDelivery(delivery, transaction);
        transaction.Commit();
        return null;
    }

    public async Task<IReadOnlyList<DispatchDelivery>> DeliveriesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ReadDocuments<DispatchDelivery>("dispatch_deliveries").OrderByDescending(x => x.CreatedAt).Take(100).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkItem>> WorkItemsAsync(string? workspacePath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ReadDocuments<WorkItem>("work_items").Where(x => workspacePath is null || x.WorkspacePath == workspacePath).OrderByDescending(x => x.CreatedAt).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkItem> CreateWorkItemAsync(CreateWorkItemRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 300
            || string.IsNullOrWhiteSpace(request.WorkspacePath) || !Path.IsPathFullyQualified(request.WorkspacePath)
            || request.Objective?.Length > 4000)
            throw new QuestionException(400, "Titel, absoluter Projektpfad oder Verantwortlicher sind ungültig.");
        var owner = WorkRules.ValidateOwner(request.Owner).RequireSuccess();
        var agentId = owner switch
        {
            OperatorOwner => null,
            AgentOwner agent => agent.ThreadId,
            null => throw new QuestionException(400, "Eine Zuständigkeit ist erforderlich.")
        };
        if (agentId is not null)
        {
            var thread = await _codex.GetAsync(agentId, ct);
            if (thread.WorkspacePath != request.WorkspacePath)
                throw new QuestionException(400, "Der Agent gehört zu einem anderen Projekt.");
        }
        if (request.QuestionId is not null)
            await _questions.GetAsync(request.QuestionId, ct);
        var now = _timeProvider.GetUtcNow();
        var item = new WorkItem(Guid.NewGuid().ToString("N"), request.Title.Trim(), request.Objective,
            request.WorkspacePath, owner, request.QuestionId, new TodoWork(), now, now, 1);
        await _gate.WaitAsync(ct);
        try
        {
            SaveWorkItem(item);
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkItem> UpdateWorkItemAsync(string id, UpdateWorkItemRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var item = ReadDocuments<WorkItem>("work_items").FirstOrDefault(x => x.Id == id)
                ?? throw new QuestionException(404, "Aufgabe nicht gefunden.");
            item = WorkRules.Update(item, request, _timeProvider.GetUtcNow()).RequireSuccess();
            SaveWorkItem(item);
            return item;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task<PreparedMessage> PrepareMessageAsync(DispatchContent content, CancellationToken ct) => content switch
    {
        ComposeMessage composed => Task.FromResult(new PreparedMessage(ValidateMessage(composed.Text), composed.SourceThreadId)),
        ForwardAnswer answer => PrepareAnswerAsync(answer, ct),
        null => throw new QuestionException(400, "Nachrichteninhalt ist erforderlich.")
    };

    private async Task<PreparedMessage> PrepareAnswerAsync(ForwardAnswer request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.QuestionId))
            throw new QuestionException(400, "Eine Frage-ID ist erforderlich.");
        var question = await _questions.GetAsync(request.QuestionId, ct);
        var answer = question.State switch
        {
            AnsweredQuestion answered => answered.Text,
            PendingQuestion => throw new QuestionException(409, "Nur beantwortete Fragen können zugestellt werden."),
            DismissedQuestion => throw new QuestionException(409, "Zurückgezogene Fragen können nicht zugestellt werden."),
            null => throw new InvalidOperationException("Question state is not initialized.")
        };
        var choice = question.Choices.FirstOrDefault(item => item.Label == answer);
        var message = $"Antwort des Operators auf Frage {question.Id}:\n{question.Prompt}\n\nAntwort: {answer}";
        if (!string.IsNullOrWhiteSpace(question.Goal))
            message += $"\nZiel: {question.Goal}";
        var followUp = choice?.FollowUpPrompt ?? question.FollowUpPrompt;
        if (!string.IsNullOrWhiteSpace(followUp))
            message += $"\nVorgeschlagener Folgeprompt: {followUp}";
        if (!string.IsNullOrWhiteSpace(request.Note))
            message += $"\nZusatz des Operators: {ValidateMessage(request.Note)}";
        return new PreparedMessage(message, question.Origin?.ThreadId);
    }

    private static string ValidateMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 12000)
            throw new QuestionException(400, "Die Nachricht muss 1 bis 12000 Zeichen enthalten.");
        return message.Trim();
    }

    private sealed record PreparedMessage(string Text, string? SourceThreadId);

    private DispatchBatch Batch(string id, string key) => new(id, key, ReadDocuments<DispatchDelivery>("dispatch_deliveries").Where(x => x.BatchId == id).ToArray());
    private List<T> ReadDocuments<T>(string table)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT document FROM " + table; // table names are internal constants only.
        using var reader = command.ExecuteReader();
        var items = new List<T>();
        while (reader.Read())
            items.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)!);
        return items;
    }
    private void SaveDelivery(DispatchDelivery value, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO dispatch_deliveries(id,batch_id,document) VALUES($id,$batch,$doc) ON CONFLICT(id) DO UPDATE SET document=$doc";
        command.Parameters.AddWithValue("$id", value.Id);
        command.Parameters.AddWithValue("$batch", value.BatchId);
        command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(value, Json));
        command.ExecuteNonQuery();
    }
    private void SaveWorkItem(WorkItem value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO work_items(id,workspace_path,document) VALUES($id,$path,$doc) ON CONFLICT(id) DO UPDATE SET document=$doc";
        command.Parameters.AddWithValue("$id", value.Id);
        command.Parameters.AddWithValue("$path", value.WorkspacePath);
        command.Parameters.AddWithValue("$doc", JsonSerializer.Serialize(value, Json));
        command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
        _sendSlots.Dispose();
    }
}
