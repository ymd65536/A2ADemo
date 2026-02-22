using A2A;
using System.Diagnostics;
using A2A.AspNetCore; // ← これも必要かもしれません
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics; // これが必要

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetryの設定
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("A2A-Server")) // 名前を変えて識別しやすく
    .WithTracing(tracing => tracing
        .AddSource("*") // サーバー側の内部ソースをカバー
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter()) // 環境変数で HTTP/Protobuf に向ける
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation() // サーバーへのリクエスト統計
        .AddRuntimeInstrumentation()    // CPU/メモリの統計
        .AddPrometheusExporter());      // Prometheus用の出力を有効化

var app = builder.Build();

// A2A エンドポイント処理の前段で動作する簡易 HTTP リクエストログ用ミドルウェア
app.Use(async (context, next) => {
    Console.WriteLine($"[HTTP Request] {context.Request.Method} {context.Request.Path}");
    await next();
});

app.UseOpenTelemetryPrometheusScrapingEndpoint();

// 1. TaskManager の作成（タスクのライフサイクル管理）
var taskManager = new TaskManager();

// 2. エージェントのロジックを登録
var myAgent = new SimpleAgent();
myAgent.Attach(taskManager);

// 3. A2A エンドポイントのマッピング
app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

public class SimpleAgent
{
    private static readonly ActivitySource MyAgentSource = new ActivitySource("MyAgent.Custom");

    public void Attach(ITaskManager taskManager)
    {
        // メッセージ受信時の処理を登録
        taskManager.OnMessageReceived = ProcessMessageAsync;
        // エージェント情報の問い合わせに対する応答を登録
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private Task<A2AResponse> ProcessMessageAsync(MessageSendParams messageParams, CancellationToken ct)
    {
        using var activity = MyAgentSource.StartActivity("ロジック実行中");

        // 送信されたテキストを取得
        var userText = messageParams.Message.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";

        // 応答メッセージの作成
        var responseMessage = new AgentMessage
        {
            Role = MessageRole.Agent,
            MessageId = Guid.NewGuid().ToString(),
            Parts = [new TextPart { Text = $"[A2A応答] あなたは「{userText}」と言いましたね。" }]
        };

        return Task.FromResult<A2AResponse>(responseMessage);
    }

    private Task<AgentCard> GetAgentCardAsync(string agentUrl, CancellationToken ct)
    {
        // エージェントの機能定義（エージェントカード）を返す
        return Task.FromResult(new AgentCard
        {
            Name = "サンプル .NET エージェント",
            Description = "A2Aプロトコルで通信するデモ用エージェントです。",
            Url = agentUrl,
            Capabilities = new AgentCapabilities {
                Streaming = false,
                Extensions = new()
            }
        });
    }
}
