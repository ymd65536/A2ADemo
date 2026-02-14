var builder = WebApplication.CreateBuilder(args);

// もし AddAgents() でエラーが出る場合は、一旦コメントアウトして
// 標準的な DI 設定のみを残します
builder.Services.AddHttpClient(); 

var app = builder.Build();

// A2A 看板 (Agent Card)
app.MapGet("/.well-known/agent-card.json", () => new {
    schema_version = "1.0",
    name = "SDK-Agent-Net10",
    description = "Microsoft Agents SDK (Preview) を使用したエージェント",
    capabilities = new[] { "process" },
    endpoints = new { a2a_rpc = "/rpc" }
});

// A2A 窓口 (RPC)
app.MapPost("/rpc", async (HttpContext context) => {
    try {
        var json = await context.Request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return Results.Ok(new { 
            jsonrpc = "2.0", 
            result = "SDKエージェントが正常に応答しました", 
            id = json.TryGetProperty("id", out var id) ? id.GetRawText() : "null"
        });
    } catch {
        return Results.BadRequest();
    }
});

app.Run();