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
    .ConfigureResource(r => r.AddService("EvaluationAgent"))
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
var agent = new EvaluationAgent(config, app.Logger);
agent.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

// ─────────────────────────────────────────────
// EvaluationAgent
// ─────────────────────────────────────────────
// 役割:
//   "[User]\n{質問}\n[Chatbot]\n{回答}" 形式のペアを受け取り、
//   内容に応じて ViolenceEvaluator / SexualEvaluator を
//   A2A プロトコルで呼び出して安全性を評価する。
//   評価結果を Q&A ペアとともに返す。
//
// リクエスト形式:
//   A2A message/send に以下のテキストを送る
//   [User]
//   ユーザーの質問
//   [Chatbot]
//   Chatbotの回答
//
// レスポンス形式:
//   [User]
//   ユーザーの質問
//   [Chatbot]
//   Chatbotの回答
//   [Evaluation]
//   Violence: score=N (Severity) flagged=bool
//   Sexual:   score=N (Severity) flagged=bool  ※ 呼び出した場合のみ
// ─────────────────────────────────────────────
public class EvaluationAgent(IConfiguration config, ILogger logger)
{
    private static readonly ActivitySource Source = new("EvaluationAgent.Custom");

    // 暴力的コンテンツ判定キーワード
    private static readonly string[] ViolenceTriggerKeywords =
    [
        "kill", "attack", "violence", "bomb", "murder", "shoot", "stab", "weapon", "fight",
        "殺", "暴力", "攻撃", "爆弾", "射撃", "武器", "戦闘"
    ];

    // 性的コンテンツ判定キーワード
    private static readonly string[] SexualTriggerKeywords =
    [
        "sex", "nude", "explicit", "sexual", "porn", "naked", "erotic",
        "性的", "裸", "ポルノ", "エロ", "わいせつ",
        "近親相姦", "アダルト", "18禁", "r-18", "r18"
    ];

    private enum ContentCategory { None, Violence, Sexual }

    public void Attach(ITaskManager taskManager)
    {
        taskManager.OnMessageReceived = EvaluateAsync;
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private async Task<A2AResponse> EvaluateAsync(MessageSendParams messageParams, CancellationToken ct)
    {
        using var activity = Source.StartActivity("Q&A評価処理");

        var inputText = messageParams.Message.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "";

        // "[User]\n...\n[Chatbot]\n..." 形式からパース
        var (userQuestion, chatbotAnswer) = ParseQAPair(inputText);

        logger.LogInformation("[EvaluationAgent] 評価開始 user={User} chatbot={Chatbot}",
            userQuestion[..Math.Min(80, userQuestion.Length)],
            chatbotAnswer[..Math.Min(80, chatbotAnswer.Length)]);

        activity?.SetTag("user.question.length", userQuestion.Length);
        activity?.SetTag("chatbot.answer.length", chatbotAnswer.Length);

        // 評価対象テキスト = ユーザーの質問 + Chatbotの回答 を結合
        var evalText = $"{userQuestion} {chatbotAnswer}";
        var evalPayload = JsonSerializer.Serialize(new { text = evalText });

        var violenceUrl = config["Evaluators:ViolenceEvaluatorUrl"]
            ?? "http://violence-evaluator-svc";
        var sexualUrl = config["Evaluators:SexualEvaluatorUrl"]
            ?? "http://sexual-evaluator-svc";

        // コンテンツを分類して適切な Evaluator の AgentCard を読み込み、呼び出す
        // 暴力的 → ViolenceEvaluator、性的 → SexualEvaluator
        // 区別がつかない場合はキーワードスコアが高い方を選択。同点は Violence を優先
        var category = ClassifyContent(evalText);

        logger.LogInformation("[EvaluationAgent] コンテンツ分類: {Category}", category);
        activity?.SetTag("content.category", category.ToString());

        EvalResult? violenceResult = null;
        EvalResult? sexualResult = null;

        switch (category)
        {
            case ContentCategory.Violence:
                violenceResult = await CallEvaluatorAsync(violenceUrl, evalPayload, ct);
                break;
            case ContentCategory.Sexual:
                sexualResult = await CallEvaluatorAsync(sexualUrl, evalPayload, ct);
                break;
            case ContentCategory.None:
            default:
                logger.LogInformation("[EvaluationAgent] センシティブなコンテンツなし。評価をスキップします。");
                break;
        }

        activity?.SetTag("violence.flagged", violenceResult?.Flagged);
        activity?.SetTag("sexual.flagged", sexualResult?.Flagged);

        // ─── レスポンス構築 ───
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[User]");
        sb.AppendLine(userQuestion);
        sb.AppendLine("[Chatbot]");
        sb.AppendLine(chatbotAnswer);
        sb.AppendLine("[Evaluation]");

        if (category == ContentCategory.None)
        {
            sb.AppendLine("センシティブなコンテンツは検出されませんでした。");
        }
        else if (category == ContentCategory.Violence)
        {
            if (violenceResult is not null)
            {
                var flag = violenceResult.Flagged ? "⚠️ flagged" : "✓ safe";
                sb.AppendLine($"Violence: score={violenceResult.Score} ({violenceResult.Severity}) {flag}");
            }
            else
            {
                sb.AppendLine("Violence: (評価失敗)");
            }
        }
        else if (category == ContentCategory.Sexual)
        {
            if (sexualResult is not null)
            {
                var flag = sexualResult.Flagged ? "⚠️ flagged" : "✓ safe";
                sb.AppendLine($"Sexual:   score={sexualResult.Score} ({sexualResult.Severity}) {flag}");
            }
            else
            {
                sb.AppendLine("Sexual:   (評価失敗)");
            }
        }

        var anyFlagged = violenceResult?.Flagged == true || sexualResult?.Flagged == true;
        sb.AppendLine(anyFlagged ? "\n総合判定: ⚠️ 問題のあるコンテンツを検出しました" : "\n総合判定: ✅ 安全性チェック通過");

        logger.LogInformation("[EvaluationAgent] 評価完了 violence={VFlag} sexual={SFlag}",
            violenceResult?.Flagged, sexualResult?.Flagged);

        return BuildReply(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// "[User]\n...\n[Chatbot]\n..." 形式のテキストから Q&A ペアを抽出する
    /// </summary>
    private static (string userQuestion, string chatbotAnswer) ParseQAPair(string input)
    {
        var userQuestion = string.Empty;
        var chatbotAnswer = string.Empty;

        var lines = input.Split('\n');
        var section = string.Empty;
        var current = new System.Text.StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed == "[User]")
            {
                if (section == "[Chatbot]") chatbotAnswer = current.ToString().Trim();
                section = "[User]";
                current.Clear();
            }
            else if (trimmed == "[Chatbot]")
            {
                if (section == "[User]") userQuestion = current.ToString().Trim();
                section = "[Chatbot]";
                current.Clear();
            }
            else if (section.Length > 0)
            {
                current.AppendLine(line);
            }
        }

        // 最後のセクションを確定
        if (section == "[Chatbot]") chatbotAnswer = current.ToString().Trim();
        else if (section == "[User]") userQuestion = current.ToString().Trim();

        // パースできない場合はテキスト全体を userQuestion として扱う
        if (string.IsNullOrEmpty(userQuestion) && string.IsNullOrEmpty(chatbotAnswer))
            userQuestion = input;

        return (userQuestion, chatbotAnswer);
    }

    /// <summary>コンテンツを分類して適切な評価カテゴリを返す</summary>
    /// <remarks>
    /// 暴力・性的両方のキーワードが一致する場合はスコアが高い方を返す。
    /// 同点の場合は Violence を優先。いずれも一致しない場合は None を返す。
    /// </remarks>
    private static ContentCategory ClassifyContent(string text)
    {
        var violenceScore = ViolenceTriggerKeywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        var sexualScore = SexualTriggerKeywords.Count(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

        if (violenceScore == 0 && sexualScore == 0) return ContentCategory.None;

        // スコアが高い方に分類。同点の場合は Violence を優先
        return sexualScore > violenceScore ? ContentCategory.Sexual : ContentCategory.Violence;
    }

    /// <summary>評価エージェントを A2A で呼び出す</summary>
    private async Task<EvalResult?> CallEvaluatorAsync(string baseUrl, string evalPayload, CancellationToken ct)
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
                    Parts = [new TextPart { Text = evalPayload }]
                }
            }, ct);

            if (response is AgentMessage agentMsg)
            {
                var resultText = agentMsg.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "{}";
                return JsonSerializer.Deserialize<EvalResult>(resultText,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[EvaluationAgent] 評価エージェント呼び出し失敗: {Url}", baseUrl);
        }
        return null;
    }

    private static AgentMessage BuildReply(string text) => new()
    {
        Role = MessageRole.Agent,
        MessageId = Guid.NewGuid().ToString(),
        Parts = [new TextPart { Text = text }]
    };

    private Task<AgentCard> GetAgentCardAsync(string agentUrl, CancellationToken ct) =>
        Task.FromResult(new AgentCard
        {
            Name = "EvaluationAgent",
            Description = "ユーザーの質問と Chatbot の回答ペアを受け取り、Violence / Sexual 評価エージェントを呼び出してコンテンツの安全性を評価します。",
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
