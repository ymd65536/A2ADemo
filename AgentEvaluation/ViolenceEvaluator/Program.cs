using A2A;
using A2A.AspNetCore;
using Azure;
using Azure.AI.ContentSafety;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("ViolenceEvaluator"))
    .WithTracing(tracing => tracing
        .AddSource("*")
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());

// Azure AI Content Safety クライアントを DI に登録
// 環境変数 AzureContentSafety__Endpoint / AzureContentSafety__ApiKey で設定
// 未設定の場合は登録しない (GetService<ContentSafetyClient>() が null を返す)
var contentSafetyEndpoint = builder.Configuration["AzureContentSafety:Endpoint"];
var contentSafetyApiKey = builder.Configuration["AzureContentSafety:ApiKey"];
if (!string.IsNullOrEmpty(contentSafetyEndpoint) && !string.IsNullOrEmpty(contentSafetyApiKey))
{
    builder.Services.AddSingleton(
        new ContentSafetyClient(new Uri(contentSafetyEndpoint), new AzureKeyCredential(contentSafetyApiKey)));
}

var app = builder.Build();

app.Use(async (context, next) =>
{
    app.Logger.LogInformation("[HTTP Request] {Method} {Path}", context.Request.Method, context.Request.Path);
    await next();
});

app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

var taskManager = new TaskManager();
var csClient = app.Services.GetService<ContentSafetyClient?>();
var agent = new ViolenceEvaluatorAgent(csClient, app.Logger);
agent.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

public class ViolenceEvaluatorAgent(ContentSafetyClient? csClient, ILogger logger)
{
    private static readonly ActivitySource Source = new("ViolenceEvaluator.Custom");

    public void Attach(ITaskManager taskManager)
    {
        taskManager.OnMessageReceived = EvaluateAsync;
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private async Task<A2AResponse> EvaluateAsync(MessageSendParams messageParams, CancellationToken ct)
    {
        using var activity = Source.StartActivity("暴力コンテンツ評価");

        var inputText = messageParams.Message.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";

        // JSON 形式 {"text":"..."} でも plain text でも受け付ける
        var textToEvaluate = TryExtractText(inputText);

        int score;
        string severity;
        bool flagged;

        if (csClient is not null)
        {
            // Azure AI Content Safety で評価
            var options = new AnalyzeTextOptions(textToEvaluate);
            options.Categories.Add(TextCategory.Violence);
            var response = await csClient.AnalyzeTextAsync(options, ct);
            var result = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Violence);
            score = result?.Severity ?? 0;
            flagged = score >= 2;
            severity = score switch { 0 => "None", 2 => "Low", 4 => "Medium", _ => "High" };
        }
        else
        {
            // モード: Content Safety 未設定の場合はキーワードで簡易判定 (開発用)
            logger.LogWarning("[ViolenceEvaluator] AzureContentSafety が未設定です。簡易評価を使用します。");
            var keywords = new[] { "kill", "attack", "violence", "殺", "暴力", "攻撃" };
            flagged = keywords.Any(k => textToEvaluate.Contains(k, StringComparison.OrdinalIgnoreCase));
            score = flagged ? 4 : 0;
            severity = flagged ? "Medium" : "None";
        }

        activity?.SetTag("violence.score", score);
        activity?.SetTag("violence.flagged", flagged);

        var resultJson = JsonSerializer.Serialize(new
        {
            category = "Violence",
            score,
            severity,
            flagged
        });

        logger.LogInformation("[ViolenceEvaluator] text={Text} score={Score} flagged={Flagged}",
            textToEvaluate[..Math.Min(50, textToEvaluate.Length)], score, flagged);

        return new AgentMessage
        {
            Role = MessageRole.Agent,
            MessageId = Guid.NewGuid().ToString(),
            Parts = [new TextPart { Text = resultJson }]
        };
    }

    private Task<AgentCard> GetAgentCardAsync(string agentUrl, CancellationToken ct) =>
        Task.FromResult(new AgentCard
        {
            Name = "Violence Evaluator",
            Description = "テキストに含まれる暴力的なコンテンツを Azure AI Content Safety で評価するエージェントです。",
            Url = agentUrl,
            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                Extensions = new()
            }
        });

    private static string TryExtractText(string input)
    {
        if (input.TrimStart().StartsWith('{'))
        {
            try
            {
                var doc = JsonDocument.Parse(input);
                if (doc.RootElement.TryGetProperty("text", out var t))
                    return t.GetString() ?? input;
            }
            catch { }
        }
        return input;
    }
}
