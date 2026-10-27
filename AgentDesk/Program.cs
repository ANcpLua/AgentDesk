using System.Net;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AgentDesk.Mcp;
using AgentDesk.Middleware;
using AgentDesk.Questions;
using AgentDesk.Codex;
using AgentDesk.Infrastructure;
using AgentDesk.Workspace;

var stdio = args.Contains("--mcp-stdio", StringComparer.Ordinal);
var runtimeArgs = args.Where(x => x != "--mcp-stdio").ToArray();
var contentRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("QUESTION_INBOX_CONTENT_ROOT")
    ?? (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : Directory.GetCurrentDirectory()));
var webRoot = Path.Combine(contentRoot, "wwwroot");

if (stdio)
{
    var host = Host.CreateApplicationBuilder(runtimeArgs);
    host.Logging.ClearProviders();
    host.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    var serverUrl = Environment.GetEnvironmentVariable("QUESTION_INBOX_URL") ?? "http://127.0.0.1:5137";
    var uri = new Uri(serverUrl);
    if (!uri.IsLoopback)
        throw new InvalidOperationException("The local inbox bridge requires a loopback URL.");
    host.Services.AddSingleton(new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(60) });
    host.Services.AddSingleton<InboxHttpTransport>();
    host.Services.AddSingleton<IQuestionService, HttpQuestionService>();
    host.Services.AddSingleton<HttpWorkspaceClient>();
    host.Services.AddSingleton<ICodexClient>(sp => sp.GetRequiredService<HttpWorkspaceClient>());
    host.Services.AddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<HttpWorkspaceClient>());
    host.Services.AddSingleton(InboxAssets.Load(webRoot));
    host.Services.AddMcpServer(ConfigureMcp).WithStdioServerTransport()
        .WithTools<QuestionTools>().WithTools<WorkspaceTools>().WithResources<InboxResource>();
    await host.Build().RunAsync();
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = runtimeArgs, ContentRootPath = contentRoot, WebRootPath = webRoot });
builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
if (builder.Configuration["urls"] is null) builder.WebHost.UseUrls("http://127.0.0.1:5137");
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<QuestionExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, new RequiredDiscriminatorResolver()));
var dataPath = Environment.GetEnvironmentVariable("QUESTION_INBOX_DB")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuestionInbox", "inbox-v3.db");
builder.Services.AddSingleton<ICodexClient, CodexClient>();
builder.Services.AddSingleton<IQuestionService>(sp => new QuestionService(dataPath, sp.GetRequiredService<ICodexClient>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IWorkspaceService>(sp => new WorkspaceService(dataPath,
    sp.GetRequiredService<ICodexClient>(), sp.GetRequiredService<IQuestionService>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton(InboxAssets.Load(webRoot));
builder.Services.AddMcpServer(ConfigureMcp).WithHttpTransport().WithTools<QuestionTools>().WithTools<WorkspaceTools>().WithResources<InboxResource>();

var app = builder.Build();
_ = app.Services.GetRequiredService<IQuestionService>();
_ = app.Services.GetRequiredService<IWorkspaceService>();
app.Logger.LogInformation("Question inbox database: {DatabasePath}", dataPath);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    // Local personal service: reject remote clients and cross-origin browser requests.
    if (context.Connection.RemoteIpAddress is { } address && !IPAddress.IsLoopback(address))
    {
        context.Response.StatusCode = 403;
        return;
    }
    var origin = context.Request.Headers.Origin.ToString();
    if (!string.IsNullOrEmpty(origin) && origin != $"{context.Request.Scheme}://{context.Request.Host}")
    {
        context.Response.StatusCode = 403;
        return;
    }
    if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")
        && context.Request.Path.StartsWithSegments("/api")
        && context.Request.Headers["X-Question-Inbox"] != "1")
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    await next(context);
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapGet("/health", () => TypedResults.Ok(new { service = "question-inbox", version = "0.3.0" }));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapQuestions();
app.MapWorkspace();
app.MapMcp("/mcp");
await app.RunAsync();

static void ConfigureMcp(McpServerOptions options)
{
    options.ServerInfo = new Implementation { Name = "question-inbox", Version = "0.3.0" };
    options.ServerInstructions = "Use ask_question for durable asynchronous human questions. It returns immediately; continue unrelated work. Read get_question/list_questions at a natural continuation point, not in a polling loop. Never treat pending, missing or dismissed answers as permission. These questions are separate from native command/file approval prompts. Do not answer on behalf of the human.";
    options.Capabilities = new ServerCapabilities
    {
        Extensions = new Dictionary<string, object> { ["io.modelcontextprotocol/ui"] = new System.Text.Json.Nodes.JsonObject() }
    };
}
