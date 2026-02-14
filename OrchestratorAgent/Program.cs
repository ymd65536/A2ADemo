var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

app.MapGet("/ask", async (string tool, IHttpClientFactory factory) => {
    var client = factory.CreateClient();

    // 1. Discovery: 名前解決で相手のカードをチェック
    var targetSvc = "http://sdk-agent-svc";
    var card = await client.GetFromJsonAsync<AgentCard>($"{targetSvc}/.well-known/agent-card.json");

    // 2. Capability Check
    if (card != null && card.Capabilities.Contains(tool)) {
        // 3. A2A RPC Execution
        var response = await client.PostAsJsonAsync($"{targetSvc}{card.Endpoints.A2a_rpc}", new {
            jsonrpc = "2.0",
            method = tool,
            @params = new { },
            id = Guid.NewGuid()
        });

        var result = await response.Content.ReadAsStringAsync();
        return $"[.NET 10 Orchestrator] Result: {result}";
    }
    return "No agent found.";
});

app.Run();

public record AgentCard(string[] Capabilities, AgentEndpoints Endpoints);
public record AgentEndpoints(string A2a_rpc);
