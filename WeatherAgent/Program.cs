var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// A2A Discovery: カードを公開
app.MapGet("/.well-known/agent-card.json", () => new {
    schema_version = "1.0",
    name = "Weather-Agent",
    capabilities = new[] { "get_weather" },
    endpoints = new { a2a_rpc = "/rpc" }
});

// A2A RPC: リクエスト処理
app.MapPost("/rpc", async (HttpContext context) => {
    var req = await context.Request.ReadFromJsonAsync<A2ARpcRequest>();
    if (req?.Method == "get_weather") {
        return Results.Ok(new { 
            jsonrpc = "2.0", 
            result = "現在の東京は .NET 10 のように爽やかな快晴です。", 
            id = req.Id 
        });
    }
    return Results.BadRequest();
});

app.Run();

public record A2ARpcRequest(string Jsonrpc, string Method, object Params, object Id);
