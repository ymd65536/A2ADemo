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
var isProduction = builder.Environment.IsProduction();

if (!string.IsNullOrEmpty(contentSafetyEndpoint) && !string.IsNullOrEmpty(contentSafetyApiKey))
{
    builder.Services.AddSingleton(
        new ContentSafetyClient(new Uri(contentSafetyEndpoint), new AzureKeyCredential(contentSafetyApiKey)));
}
else if (isProduction)
{
    // Production モードでは Azure Content Safety の設定が必須
    throw new InvalidOperationException(
        "[SexualEvaluator] Production モードでは AzureContentSafety:Endpoint と AzureContentSafety:ApiKey の設定が必須です。" +
        "環境変数 AzureContentSafety__Endpoint / AzureContentSafety__ApiKey を設定してください。");
}

// 評価フレームワークを使った評価器を登録
builder.Services.AddSingleton<IContentEvaluator<SexualEvaluationResult>>(sp =>
{
    var csClient = sp.GetService<ContentSafetyClient>();
    var logger = sp.GetRequiredService<ILogger<SexualEvaluator>>();
    return new SexualEvaluator(csClient, logger, allowMock: !isProduction);
});

var app = builder.Build();

app.Use(async (context, next) =>
{
    app.Logger.LogInformation("[HTTP Request] {Method} {Path}", context.Request.Method, context.Request.Path);
    await next();
});

app.UseOpenTelemetryPrometheusScrapingEndpoint();
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

var taskManager = new TaskManager();
var evaluator = app.Services.GetRequiredService<IContentEvaluator<SexualEvaluationResult>>();
var agent = new SexualEvaluatorAgent(evaluator, app.Logger);
agent.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

// 評価フレームワークのインターフェース定義
public interface IContentEvaluator<TResult>
{
    Task<ContentEvaluationResult<TResult>> EvaluateAsync(
        string input,
        CancellationToken cancellationToken = default);
}

public record ContentEvaluationResult<TResult>
{
    public required TResult Result { get; init; }
    public Dictionary<string, object> Metrics { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
}

// 評価結果の型定義
public record SexualEvaluationResult
{
    public string Category { get; init; } = "Sexual";
    public int Score { get; init; }
    public string Severity { get; init; } = "None";
    public bool Flagged { get; init; }
}

// 評価フレームワークを使った評価器実装
public class SexualEvaluator(ContentSafetyClient? csClient, ILogger<SexualEvaluator> logger, bool allowMock)
    : IContentEvaluator<SexualEvaluationResult>
{
    public async Task<ContentEvaluationResult<SexualEvaluationResult>> EvaluateAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        var textToEvaluate = TryExtractText(input);

        int score;
        string severity;
        bool flagged;

        if (csClient is not null)
        {
            // Azure AI Content Safety で評価
            var options = new AnalyzeTextOptions(textToEvaluate);
            options.Categories.Add(TextCategory.Sexual);
            var response = await csClient.AnalyzeTextAsync(options, cancellationToken);
            var result = response.Value.CategoriesAnalysis
                .FirstOrDefault(c => c.Category == TextCategory.Sexual);
            score = result?.Severity ?? 0;
            flagged = score >= 2;
            severity = score switch { 0 => "None", 2 => "Low", 4 => "Medium", _ => "High" };
        }
        else if (allowMock)
        {
            // モック: Content Safety 未設定の場合は簡易判定 (開発用)
            logger.LogWarning("[SexualEvaluator] AzureContentSafety が未設定です。モック評価を使用します。");
            // 開発用モックは常に安全と判定
            flagged = false;
            score = 0;
            severity = "None";
        }
        else
        {
            // Production モードで csClient が null の場合は起動時に捕捉されるはずだが安全ネットとして例外
            throw new InvalidOperationException(
                "[SexualEvaluator] Production モードでは Azure Content Safety の設定が必須です。");
        }

        logger.LogInformation("[SexualEvaluator] text={Text} score={Score} flagged={Flagged}",
            textToEvaluate[..Math.Min(50, textToEvaluate.Length)], score, flagged);

        var evaluationResult = new SexualEvaluationResult
        {
            Score = score,
            Severity = severity,
            Flagged = flagged
        };

        return new ContentEvaluationResult<SexualEvaluationResult>
        {
            Result = evaluationResult,
            Metrics = new Dictionary<string, object>
            {
                ["sexual_score"] = score,
                ["sexual_severity"] = severity,
                ["sexual_flagged"] = flagged,
                ["input_length"] = textToEvaluate.Length
            },
            Metadata = new Dictionary<string, string>
            {
                ["evaluator_type"] = "Sexual",
                ["evaluation_timestamp"] = DateTime.UtcNow.ToString("O")
            }
        };
    }

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

public class SexualEvaluatorAgent(IContentEvaluator<SexualEvaluationResult> evaluator, ILogger logger)
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

        // 評価器を使用して評価
        var evaluationResult = await evaluator.EvaluateAsync(inputText, ct);
        var result = evaluationResult.Result;

        activity?.SetTag("sexual.score", result.Score);
        activity?.SetTag("sexual.flagged", result.Flagged);

        // メトリクスをアクティビティに追加
        foreach (var metric in evaluationResult.Metrics)
        {
            activity?.SetTag($"metric.{metric.Key}", metric.Value);
        }

        var resultJson = JsonSerializer.Serialize(new
        {
            category = result.Category,
            score = result.Score,
            severity = result.Severity,
            flagged = result.Flagged
        });

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
            Description = "テキストに含まれる性的なコンテンツを評価フレームワークパターンで評価するエージェントです。",
            Url = agentUrl,
            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                Extensions = new()
            }
        });
}
