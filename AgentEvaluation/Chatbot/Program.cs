using A2A;
using A2A.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("Chatbot"))
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

var taskManager = new TaskManager();
var config = app.Configuration;
var chatbot = new ChatbotAgent(config, app.Logger);
chatbot.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

// ─────────────────────────────────────────────
// Chatbot エージェント
// ─────────────────────────────────────────────
// 役割:
//   ユーザーからのメッセージを受け取り、
//   内容に応じて ViolenceEvaluator / SexualEvaluator を
//   A2A プロトコルで呼び出して安全性を評価する。
//   評価結果に問題がなければ通常の応答を返し、
//   問題があれば安全上の警告を返す。
// ─────────────────────────────────────────────
public class ChatbotAgent(IConfiguration config, ILogger logger)
{
    private static readonly ActivitySource Source = new("Chatbot.Custom");

    // 評価が必要かどうかを判断するキーワード群
    // 実際のシステムではより高度な判定ロジックに差し替えること
    private static readonly string[] EvalTriggerKeywords =
    [
        "kill", "attack", "hurt", "punch", "stab", "bomb", "shoot", "weapon",
        "殺", "攻撃", "傷", "爆発", "銃", "ナイフ", "暴力",
        "sex", "nude", "explicit", "sexual",
        "性的", "裸", "ポルノ"
    ];

    public void Attach(ITaskManager taskManager)
    {
        taskManager.OnMessageReceived = RespondAsync;
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private async Task<A2AResponse> RespondAsync(MessageSendParams messageParams, CancellationToken ct)
    {
        using var activity = Source.StartActivity("チャット応答処理");

        var userText = messageParams.Message.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";
        activity?.SetTag("user.message.length", userText.Length);

        // ─── 評価が必要か判断 ───
        var needsEval = NeedsEvaluation(userText);
        logger.LogInformation("[Chatbot] message={Message} needsEval={NeedsEval}",
            userText[..Math.Min(80, userText.Length)], needsEval);

        if (!needsEval)
        {
            // 評価不要: そのまま応答
            return BuildReply($"[Chatbot] {userText} について承りました。何かお手伝いできることはありますか？");
        }

        // ─── 評価エージェントを A2A で並列呼び出し ───
        var evalPayload = JsonSerializer.Serialize(new { text = userText });

        var violenceUrl = config["Evaluators:ViolenceEvaluatorUrl"]
            ?? "http://violence-evaluator-svc";
        var sexualUrl = config["Evaluators:SexualEvaluatorUrl"]
            ?? "http://sexual-evaluator-svc";

        EvalResult? violenceResult = null;
        EvalResult? sexualResult = null;

        await Task.WhenAll(
            CallEvaluatorAsync(violenceUrl, evalPayload, ct)
                .ContinueWith(t => violenceResult = t.Result, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Current),
            CallEvaluatorAsync(sexualUrl, evalPayload, ct)
                .ContinueWith(t => sexualResult = t.Result, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Current)
        );

        activity?.SetTag("violence.flagged", violenceResult?.Flagged);
        activity?.SetTag("sexual.flagged", sexualResult?.Flagged);

        // ─── 評価結果に応じて応答を構築 ───
        var issues = new List<string>();
        if (violenceResult?.Flagged == true)
            issues.Add($"暴力的なコンテンツ (重大度: {violenceResult.Severity})");
        if (sexualResult?.Flagged == true)
            issues.Add($"性的なコンテンツ (重大度: {sexualResult.Severity})");

        string replyText;
        if (issues.Count > 0)
        {
            replyText = $"[Chatbot] 申し訳ありませんが、入力されたメッセージに問題のあるコンテンツが含まれているため、応答できません。\n" +
                        $"検出された問題: {string.Join(", ", issues)}\n" +
                        $"Violence: score={violenceResult?.Score ?? 0} / Sexual: score={sexualResult?.Score ?? 0}";
            logger.LogWarning("[Chatbot] フラグ付きコンテンツを検出しました: {Issues}", string.Join(", ", issues));
        }
        else
        {
            replyText = $"[Chatbot] メッセージの安全性を確認しました。\n" +
                        $"Violence: score={violenceResult?.Score ?? 0} ({violenceResult?.Severity ?? "N/A"}) ✓\n" +
                        $"Sexual:   score={sexualResult?.Score ?? 0} ({sexualResult?.Severity ?? "N/A"}) ✓\n\n" +
                        $"「{userText}」のご質問にお答えします。";
        }

        return BuildReply(replyText);
    }

    /// <summary>
    /// 評価エージェントを A2A で呼び出す
    /// </summary>
    private async Task<EvalResult?> CallEvaluatorAsync(string baseUrl, string evalPayload, CancellationToken ct)
    {
        try
        {
            // AgentCard を取得してエンドポイント URL を解決
            var cardResolver = new A2ACardResolver(new Uri($"{baseUrl}/agent"));
            var card = await cardResolver.GetAgentCardAsync();
            var client = new A2AClient(new Uri(card.Url));

            // A2A message/send でテキストを送信
            var response = await client.SendMessageAsync(new MessageSendParams
            {
                Message = new AgentMessage
                {
                    Role = MessageRole.User,
                    Parts = [new TextPart { Text = evalPayload }]
                }
            }, ct);

            if (response is AgentMessage agentMsg)
            {
                var resultText = agentMsg.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "{}";
                var evalResult = JsonSerializer.Deserialize<EvalResult>(resultText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return evalResult;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Chatbot] 評価エージェント呼び出し失敗: {Url}", baseUrl);
        }
        return null;
    }

    /// <summary>
    /// コンテンツ評価が必要かどうかをキーワードで判断する
    /// </summary>
    private static bool NeedsEvaluation(string text) =>
        EvalTriggerKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static AgentMessage BuildReply(string text) => new()
    {
        Role = MessageRole.Agent,
        MessageId = Guid.NewGuid().ToString(),
        Parts = [new TextPart { Text = text }]
    };

    private Task<AgentCard> GetAgentCardAsync(string agentUrl, CancellationToken ct) =>
        Task.FromResult(new AgentCard
        {
            Name = "Chatbot",
            Description = "ユーザーとの対話を行いながら Violence / Sexual 評価エージェントを呼び出してコンテンツの安全性を確認するチャットボットです。",
            Url = agentUrl,
            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                Extensions = new()
            }
        });
}

/// <summary>評価エージェントからの応答を表す</summary>
public record EvalResult(string Category, int Score, string Severity, bool Flagged);
