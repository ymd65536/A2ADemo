using A2A;
using A2A.AspNetCore;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
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

// ─── Azure OpenAI IChatClient (Microsoft.Extensions.AI) ───
// AzureOpenAI:Endpoint / AzureOpenAI:DeploymentName が設定されている場合のみ有効化
// 認証: AzureOpenAI:ApiKey が設定されていれば AzureKeyCredential を使用
//       未設定の場合は DefaultAzureCredential にフォールバック (AKS Workload Identity 用)
var openAiEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
var openAiDeployment = builder.Configuration["AzureOpenAI:DeploymentName"];
var openAiApiKey = builder.Configuration["AzureOpenAI:ApiKey"];

if (!string.IsNullOrEmpty(openAiEndpoint) && !string.IsNullOrEmpty(openAiDeployment))
{
    AzureOpenAIClient openAiClient = !string.IsNullOrEmpty(openAiApiKey)
        ? new AzureOpenAIClient(new Uri(openAiEndpoint), new AzureKeyCredential(openAiApiKey))
        : new AzureOpenAIClient(new Uri(openAiEndpoint), new DefaultAzureCredential());

    ChatClient chatClientInstance = openAiClient.GetChatClient(openAiDeployment);

    builder.Services.AddSingleton<ChatClient>(chatClientInstance);
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
var config = app.Configuration;
var chatClient = app.Services.GetService<ChatClient>();
app.Logger.LogInformation("[Startup] AzureOpenAI:Endpoint='{E}' AzureOpenAI:DeploymentName='{D}' ChatClient={C}",
    config["AzureOpenAI:Endpoint"] ?? "(null)",
    config["AzureOpenAI:DeploymentName"] ?? "(null)",
    chatClient?.GetType().Name ?? "null (not registered)");
var chatbot = new ChatbotAgent(chatClient, config, app.Logger);
chatbot.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

// ─────────────────────────────────────────────
// Chatbot エージェント
// ─────────────────────────────────────────────
// 役割:
//   ユーザーからのメッセージを Azure OpenAI (Microsoft.Extensions.AI) で処理し、
//   センシティブなキーワードを含む場合は EvaluationAgent を A2A で呼び出して
//   Q&A ペアの安全性を評価する。
//
// リクエストごとに [system, user] の2メッセージのみ送信 (会話履歴は保持しない)
// Azure OpenAI 未設定時はモック応答にフォールバック (開発用)
// ─────────────────────────────────────────────
public class ChatbotAgent(ChatClient? chatClient, IConfiguration config, ILogger logger)
{
    private static readonly ActivitySource Source = new("Chatbot.Custom");

    // 評価エージェントを起動するキーワード群
    private static readonly string[] EvalTriggerKeywords =
    [
        "kill", "attack", "hurt", "punch", "stab", "bomb", "shoot", "weapon",
        "殺", "攻撃", "傷", "爆発", "銃", "ナイフ", "暴力",
        "sex", "nude", "explicit", "sexual",
        "性的", "裸", "ポルノ", "性的描写", "近親相姦", "アダルト"
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

        var needsEval = NeedsEvaluation(userText);
        logger.LogInformation("[Chatbot] message={Message} needsEval={NeedsEval} chatClientEnabled={ChatEnabled}",
            userText[..Math.Min(80, userText.Length)], needsEval, chatClient is not null);

        // ─── LLM または モック で応答を生成 ───
        string chatbotAnswer;
        if (chatClient is not null)
        {
            try
            {
                // リクエストごとに [system, user] の2メッセージのみ送信 (履歴なし)
                List<OpenAI.Chat.ChatMessage> messages =
                [
                    new SystemChatMessage("あなたは親切なアシスタントです。ユーザーの質問に対して日本語で丁寧に答えてください。"),
                    new UserChatMessage(userText)
                ];
                var completion = await chatClient.CompleteChatAsync(messages);
                chatbotAnswer = completion.Value.Content[0].Text;
                logger.LogInformation("[Chatbot] ChatClient 応答完了 length={Length}", chatbotAnswer.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Chatbot] ChatClient 呼び出し失敗。モック応答を使用します。");
                chatbotAnswer = $"{userText[..Math.Min(40, userText.Length)]} について承りました。";
            }
        }
        else
        {
            // モック応答 (Azure OpenAI 未設定時の開発用フォールバック)
            logger.LogWarning("[Chatbot] AzureOpenAI が未設定です。モック応答を使用します。");
            chatbotAnswer = needsEval
                ? "申し訳ありませんが、そのご質問にはお答えできません。"
                : $"{userText} について承りました。何かお手伝いできることはありますか？";
        }

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
        return BuildReply(evaluationResult ?? qaPair);
    }

    /// <summary>EvaluationAgent を A2A で呼び出し、評価済みテキストを返す</summary>
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
            Description = "Azure OpenAI (Microsoft Agent Framework) を使用してユーザーと対話し、センシティブなコンテンツは EvaluationAgent で安全性評価を行うチャットボットです。",
            Url = agentUrl,
            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                Extensions = new()
            }
        });
}



