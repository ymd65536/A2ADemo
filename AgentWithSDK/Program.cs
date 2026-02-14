using Microsoft.Agent.Framework;
using Microsoft.Agent.Framework.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// 1. Agent Framework のサービス登録
builder.Services.AddAgentFramework(agents =>
{
    // 天気予報能力を持つエージェントを定義
    agents.AddAgent<WeatherAgent>("Weather-Agent", agent =>
    {
        agent.Description = "MAF SDKを使用した気象情報エージェント";
        agent.Capabilities.Add("get_weather");
    });
});

var app = builder.Build();

// 2. A2A 規格のエンドポイントを自動生成
// SDK が /.well-known/agent-card.json と /rpc を自動でハンドリングします
app.MapAgentEndpoints();

app.Run();

// --- Agent Logic ---
public class WeatherAgent : IAgent
{
    // MAF SDKが JSON-RPC のリクエストをこのメソッドに振り分けてくれる
    public async Task<AgentResponse> ExecuteAsync(AgentRequest request)
    {
        if (request.Method == "get_weather")
        {
            return new AgentResponse
            {
                Result = "MAF SDK経由で取得: 東京は .NET 10 と同様に絶好調な天気です。"
            };
        }
        return AgentResponse.Error(-32601, "Method not found");
    }
}
