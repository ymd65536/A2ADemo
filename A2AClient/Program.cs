using A2A;
using OpenTelemetry;
using OpenTelemetry.Resources; // AddService はこの名前空間の拡張メソッドです
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetryの設定
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("OrchestratorClient"))
    .WithTracing(tracing => tracing
        .AddSource("*") // 全てを対象にする
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter() // ログに出すために必須
        .AddOtlpExporter());  // Aspireに飛ばすために必須

var app = builder.Build();

// エージェントへの接続設定
var serverUri = new Uri("http://a2a-server-svc/agent");
var cardResolver = new A2ACardResolver(serverUri);

// 外部から「http://(PodのIP):8080/ask?text=こんにちは」のように叩けるようにする
app.MapGet("/ask", async (string text) =>
{
    // 毎回カードを解決して、最新のエージェントURLを取得
    var agentCard = await cardResolver.GetAgentCardAsync();
    var client = new A2AClient(new Uri(agentCard.Url));

    // エージェントにメッセージを送信
    var response = await client.SendMessageAsync(new MessageSendParams
    {
        Message = new AgentMessage
        {
            Role = MessageRole.User,
            Parts = [new TextPart { Text = text }]
        }
    });

    if (response is AgentMessage agentMessage)
    {
        return agentMessage.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "返答なし";
    }
    return "エラー";
});

// app.Run() により、プロセスは終了せずリクエストを待ち続ける
app.Run();