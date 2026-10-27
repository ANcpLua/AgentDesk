using System.Text.Json;
using Microsoft.Data.Sqlite;
using AgentDesk.Codex;
using AgentDesk.Domain;
using AgentDesk.Infrastructure;

namespace AgentDesk.Questions;

public sealed class QuestionService : IQuestionService, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ICodexClient _codex;
    private readonly TimeProvider _timeProvider;

    public QuestionService(string databasePath, ICodexClient codex, TimeProvider timeProvider)
    {
        _codex = codex;
        _timeProvider = timeProvider;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS questions (
                id TEXT PRIMARY KEY, request_key TEXT UNIQUE, project TEXT NOT NULL,
                conversation_id TEXT, status TEXT NOT NULL, created_at TEXT NOT NULL, document TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_questions_scope ON questions(project, conversation_id, status);
            CREATE TABLE IF NOT EXISTS changes (revision INTEGER PRIMARY KEY AUTOINCREMENT, question_id TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<QuestionResponse> CreateAsync(CreateQuestionRequest request, CancellationToken cancellationToken)
    {
        request = QuestionRules.Prepare(request).RequireSuccess();

        QuestionOrigin? origin = null;
        if (request.OriginThreadId is not null)
        {
            var thread = await _codex.GetAsync(request.OriginThreadId, cancellationToken);
            origin = new(thread.Id, thread.SessionId, thread.WorkspacePath, thread.Name,
                thread.ParentThreadId, thread.ForkedFromId, thread.TranscriptPath, _timeProvider.GetUtcNow());
        }
        var question = new QuestionResponse(Guid.NewGuid().ToString("N"), request.Prompt,
            request.Choices!, request.AllowFreeText, request.Context,
            origin?.WorkspacePath ?? request.Project, request.ConversationId, request.Source, request.Blocking,
            new PendingQuestion(), _timeProvider.GetUtcNow(), 1, request.RequestKey,
            request.Goal, request.FollowUpPrompt, origin);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return SaveQuestion(request, question);
        }
        finally
        {
            _gate.Release();
        }
    }

    private QuestionResponse SaveQuestion(CreateQuestionRequest request, QuestionResponse question)
    {
        if (request.RequestKey is not null)
        {
            using var existing = _connection.CreateCommand();
            existing.CommandText = "SELECT document FROM questions WHERE request_key = $key";
            existing.Parameters.AddWithValue("$key", request.RequestKey);
            if (existing.ExecuteScalar() is string document)
            {
                var previous = JsonSerializer.Deserialize<QuestionResponse>(document, Json)!;
                if (previous.Prompt != question.Prompt || !previous.Choices.SequenceEqual(question.Choices)
                    || previous.AllowFreeText != question.AllowFreeText || previous.Context != question.Context
                    || previous.Project != question.Project || previous.ConversationId != question.ConversationId
                    || previous.Source != question.Source || previous.Blocking != question.Blocking
                    || previous.Goal != question.Goal || previous.FollowUpPrompt != question.FollowUpPrompt
                    || previous.Origin?.ThreadId != question.Origin?.ThreadId)
                    throw new QuestionException(409, "Dieser Request-Key gehört bereits zu einer anderen Frage.");
                return previous;
            }
        }
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO questions (id, request_key, project, conversation_id, status, created_at, document)
            VALUES ($id, $key, $project, $conversation, $status, $created, $document)
            """;
        command.Parameters.AddWithValue("$id", question.Id);
        command.Parameters.AddWithValue("$key", (object?)question.RequestKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$project", question.Project);
        command.Parameters.AddWithValue("$conversation", (object?)question.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", question.State.Kind());
        command.Parameters.AddWithValue("$created", question.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(question, Json));
        command.ExecuteNonQuery();
        RecordChange(question.Id, transaction);
        transaction.Commit();
        return question;
    }

    public async Task<QuestionSnapshot> ListAsync(QuestionFilter filter, CancellationToken cancellationToken)
    {
        if (filter.Status is not (null or "pending" or "answered" or "dismissed"))
            throw new QuestionException(400, "Unbekannter Status.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return ReadSnapshot(filter);
        }
        finally
        {
            _gate.Release();
        }
    }

    private QuestionSnapshot ReadSnapshot(QuestionFilter filter)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT document FROM questions
            WHERE ($project IS NULL OR project = $project)
              AND ($conversation IS NULL OR conversation_id = $conversation)
              AND ($status IS NULL OR status = $status)
            ORDER BY created_at DESC, id DESC
            """;
        command.Parameters.AddWithValue("$project", (object?)filter.Project ?? DBNull.Value);
        command.Parameters.AddWithValue("$conversation", (object?)filter.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (object?)filter.Status ?? DBNull.Value);
        var questions = new List<QuestionResponse>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                questions.Add(JsonSerializer.Deserialize<QuestionResponse>(reader.GetString(0), Json)!);
        command.CommandText = "SELECT COALESCE(MAX(revision), 0) FROM changes";
        command.Parameters.Clear();
        return new QuestionSnapshot((long)command.ExecuteScalar()!, questions);
    }

    public async Task<QuestionResponse> GetAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return ReadQuestion(id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<QuestionResponse> AnswerAsync(string id, AnswerQuestionRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return SaveAnswer(id, request);
        }
        finally
        {
            _gate.Release();
        }
    }

    private QuestionResponse SaveAnswer(string id, AnswerQuestionRequest request)
    {
        var question = ReadQuestion(id);
        var updated = QuestionRules.Decide(question, request, _timeProvider.GetUtcNow()).RequireSuccess();
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE questions SET status = $status, document = $document WHERE id = $id";
        command.Parameters.AddWithValue("$status", updated.State.Kind());
        command.Parameters.AddWithValue("$document", JsonSerializer.Serialize(updated, Json));
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        RecordChange(id, transaction);
        transaction.Commit();
        return updated;
    }

    private QuestionResponse ReadQuestion(string id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT document FROM questions WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is string document
            ? JsonSerializer.Deserialize<QuestionResponse>(document, Json)!
            : throw new QuestionException(404, "Die Frage wurde nicht gefunden.");
    }

    private void RecordChange(string id, SqliteTransaction transaction)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO changes (question_id) VALUES ($id)";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        _gate.Dispose();
    }
}
