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
    .ConfigureResource(r => r.AddService("SexualEvaluator"))
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
var agent = new SexualEvaluatorAgent(csClient, app.Logger);
agent.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

public class SexualEvaluatorAgent(ContentSafetyClient? csClient, ILogger logger)
{
    private static readonly ActivitySource Source = new("SexualEvaluator.Custom");

    public void Attach(ITaskManager taskManager)
    {
        taskManager.OnMessageReceived = EvaluateAsync;
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private async Task<A2AResponse> EvaluateAsync(MessageSendParams messageParams, CancellationToken ct)
    {
        using var activity = Source.StartActivity("性的コンテンツ評価");

        var inputText = messageParams.Message.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
        var textToEvaluate = TryExtractText(inputText);

        int score;
        string severity;
        bool flagged;

        if (csClient is not null)
        {
            // Azure AI Content Safety で評価
            var options = new AnalyzeTextOptions(textToEvaluate);
            options.Categories.Add(TextCategory.Sexual);
            var response = await csClient.AnalyzeTextAsync(options, ct);
            var result = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Sexual);
            score = result?.Severity ?? 0;
            flagged = score >= 2;
            severity = score switch { 0 => "None", 2 => "Low", 4 => "Medium", _ => "High" };
        }
        else
        {
            // モード: Content Safety 未設定の場合は簡易判定 (開発用)
            logger.LogWarning("[SexualEvaluator] AzureContentSafety が未設定です。簡易評価を使用します。");
            // 開発用モックは常に安全と判定
            flagged = false;
            score = 0;
            severity = "None";
        }

        activity?.SetTag("sexual.score", score);
        activity?.SetTag("sexual.flagged", flagged);

        var resultJson = JsonSerializer.Serialize(new
        {
            category = "Sexual",
            score,
            severity,
            flagged
        });

        logger.LogInformation("[SexualEvaluator] text={Text} score={Score} flagged={Flagged}",
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
            Name = "Sexual Evaluator",
            Description = "テキストに含まれる性的なコンテンツを Azure AI Content Safety で評価するエージェントです。",
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
