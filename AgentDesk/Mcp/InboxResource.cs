using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AgentDesk.Mcp;

[McpServerResourceType]
public sealed class InboxResource(InboxAssets assets)
{
    [McpServerResource(UriTemplate = "ui://question-inbox/view.html", Name = "question_inbox",
        Title = "Fragenpostfach", MimeType = "text/html;profile=mcp-app")]
    [Description("A durable human questions inbox with choices, free text and answered history.")]
    public TextResourceContents Read() => new()
    {
        Uri = "ui://question-inbox/view.html",
        MimeType = "text/html;profile=mcp-app",
        Text = assets.Html,
        Meta = new JsonObject
        {
            ["ui"] = new JsonObject
            {
                ["prefersBorder"] = true,
                ["csp"] = new JsonObject { ["connectDomains"] = new JsonArray(), ["resourceDomains"] = new JsonArray() }
            }
        }
    };
}

public sealed record InboxAssets(string Html)
{
    public static InboxAssets Load(string webRoot)
    {
        var html = File.ReadAllText(Path.Combine(webRoot, "index.html"));
        html = html.Replace("<link rel=\"stylesheet\" href=\"/inbox.css\">",
            "<style>" + File.ReadAllText(Path.Combine(webRoot, "inbox.css")) + "</style>");
        html = html.Replace("<script src=\"/inbox.js\" defer></script>",
            "<script>window.__MCP_INBOX__ = true;</script><script>" +
            File.ReadAllText(Path.Combine(webRoot, "inbox.js")) + "</script>");
        return new(html);
    }
}
