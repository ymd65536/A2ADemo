using A2A;
using System.Diagnostics;
using A2A.AspNetCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

var builder = WebApplication.CreateBuilder(args);

// OpenTelemetry の設定
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("EchoAgent"))
    .WithTracing(tracing => tracing
        .AddSource("*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

var app = builder.Build();

app.Use(async (context, next) =>
{
    app.Logger.LogInformation("[HTTP Request] {Method} {Path}", context.Request.Method, context.Request.Path);
    await next();
});

app.UseOpenTelemetryPrometheusScrapingEndpoint();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

// 1. TaskManager の作成
var taskManager = new TaskManager();

// 2. エージェントのロジックを登録
var echoAgent = new EchoAgent();
echoAgent.Attach(taskManager);

// 3. A2A エンドポイントのマッピング
app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

public class EchoAgent
{
    private static readonly ActivitySource Source = new ActivitySource("EchoAgent.Custom");

    public void Attach(ITaskManager taskManager)
    {
        taskManager.OnMessageReceived = ProcessMessageAsync;
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private Task<A2AResponse> ProcessMessageAsync(MessageSendParams messageParams, CancellationToken ct)
    {
        using var activity = Source.StartActivity("エコー処理中");

        var userText = messageParams.Message.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";

        var responseMessage = new AgentMessage
        {
            Role = MessageRole.Agent,
            MessageId = Guid.NewGuid().ToString(),
            Parts = [new TextPart { Text = $"[Echo] {userText}" }]
        };

        return Task.FromResult<A2AResponse>(responseMessage);
    }

    private Task<AgentCard> GetAgentCardAsync(string agentUrl, CancellationToken ct)
    {
        return Task.FromResult(new AgentCard
        {
            Name = "エコーエージェント",
            Description = "受け取ったメッセージをそのまま返すエコーエージェントです。",
            Url = agentUrl,
            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                Extensions = new()
            }
        });
    }
}
