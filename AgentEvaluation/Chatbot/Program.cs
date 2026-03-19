using A2A;
using A2A.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;

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

        // モック応答 (本番では LLM の応答に差し替え)
        var chatbotAnswer = needsEval
            ? "申し訳ありませんが、そのご質問にはお答えできません。"
            : $"{userText} について承りました。何かお手伝いできることはありますか？";

        var qaPair = $"[User]\n{userText}\n[Chatbot]\n{chatbotAnswer}";

        if (!needsEval)
        {
            // 評価不要: そのまま応答
            return BuildReply(qaPair);
        }

        // ─── EvaluationAgent を A2A で呼び出し (Q&A ペアを渡す) ───
        var evaluationAgentUrl = config["Evaluators:EvaluationAgentUrl"]
            ?? "http://evaluation-agent-svc";

        var evaluationResult = await CallEvaluationAgentAsync(evaluationAgentUrl, qaPair, ct);

        activity?.SetTag("evaluation.result.length", evaluationResult?.Length ?? 0);

        // EvaluationAgent の応答をそのまま返す (フォールバック: Q&A ペアのみ)
        return BuildReply(evaluationResult ?? qaPair);
    }

    /// <summary>
    /// EvaluationAgent を A2A で呼び出し、評価済みテキストを返す
    /// </summary>
    private async Task<string?> CallEvaluationAgentAsync(string baseUrl, string qaPair, CancellationToken ct)
    {
        try
        {
            var cardResolver = new A2ACardResolver(new Uri(baseUrl));
            var card = await cardResolver.GetAgentCardAsync();
            var client = new A2AClient(new Uri(card.Url));

            var response = await client.SendMessageAsync(new MessageSendParams
            {
                Message = new AgentMessage
                {
                    Role = MessageRole.User,
                    Parts = [new TextPart { Text = qaPair }]
                }
            }, ct);

            if (response is AgentMessage agentMsg)
                return agentMsg.Parts.OfType<TextPart>().FirstOrDefault()?.Text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Chatbot] EvaluationAgent 呼び出し失敗: {Url}", baseUrl);
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


