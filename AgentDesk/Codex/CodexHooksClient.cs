namespace AgentDesk.Codex;

public sealed partial class CodexClient
{
    public async Task<CodexHooks> HooksAsync(string? workspacePath, CancellationToken ct)
    {
        if (workspacePath is not null && (string.IsNullOrWhiteSpace(workspacePath) || !Path.IsPathFullyQualified(workspacePath)))
            throw new Questions.QuestionException(400, "Für die Hook-Ansicht ist ein absoluter Projektpfad erforderlich.");
        await using var rpc = await ConnectAsync(ct);
        var response = await rpc.CallAsync<CodexHooks>("hooks/list", new
        {
            cwds = workspacePath is null ? Array.Empty<string>() : [workspacePath]
        }, ct);
        if (response.Data is null || response.Data.Any(entry => entry is null || entry.Cwd is null
            || entry.Hooks is null || entry.Warnings is null || entry.Errors is null
            || entry.Warnings.Any(warning => warning is null)
            || entry.Errors.Any(error => error is null || error.Path is null || error.Message is null)
            || entry.Hooks.Any(hook => hook is null || hook.Key is null || hook.EventName is null
                || hook.SourcePath is null || hook.Source is null || hook.CurrentHash is null
                || hook.TrustStatus is null || hook.HandlerType is null
                || hook.HandlerType == "command" && (hook.Command is null || hook.Async is null)
                || hook.HandlerType == "mcpTool" && (hook.Server is null || hook.Tool is null))))
            throw new CodexConnectionException("Codex hat keine lesbaren Hook-Daten geliefert.");
        return response;
    }
}
