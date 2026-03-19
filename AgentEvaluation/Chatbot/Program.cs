using A2A;
using A2A.AspNetCore;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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

// ─── Azure OpenAI AIAgent (Microsoft Agent Framework) ───
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

    var agentInstance = openAiClient
        .GetChatClient(openAiDeployment)
        .AsIChatClient()
        .AsAIAgent(
            instructions: "あなたは親切なアシスタントです。ユーザーの質問に対して日本語で丁寧に答えてください。",
            name: "Chatbot");

    builder.Services.AddSingleton(agentInstance);
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
var aiAgent = app.Services.GetService<AIAgent>();
var chatbot = new ChatbotAgent(aiAgent, config, app.Logger);
chatbot.Attach(taskManager);

app.MapA2A(taskManager, "/agent");
app.MapWellKnownAgentCard(taskManager, "/agent");
app.Run();

// ─────────────────────────────────────────────
// Chatbot エージェント
// ─────────────────────────────────────────────
// 役割:
//   ユーザーからのメッセージを Azure OpenAI (Microsoft Agent Framework) で処理し、
//   センシティブなキーワードを含む場合は EvaluationAgent を A2A で呼び出して
//   Q&A ペアの安全性を評価する。
//
// Azure OpenAI 未設定時はモック応答にフォールバック (開発用)
// ─────────────────────────────────────────────
public class ChatbotAgent(AIAgent? aiAgent, IConfiguration config, ILogger logger)
{
    private static readonly ActivitySource Source = new("Chatbot.Custom");

    // 評価エージェントを起動するキーワード群
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

        var needsEval = NeedsEvaluation(userText);
        logger.LogInformation("[Chatbot] message={Message} needsEval={NeedsEval} aiAgentEnabled={AiEnabled}",
            userText[..Math.Min(80, userText.Length)], needsEval, aiAgent is not null);

        // ─── LLM または モック で応答を生成 ───
        string chatbotAnswer;
        if (aiAgent is not null)
        {
            try
            {
                // Microsoft Agent Framework 経由で Azure OpenAI を呼び出す
                var agentResponse = await aiAgent.RunAsync(new ChatMessage(ChatRole.User, userText));
                chatbotAnswer = agentResponse?.ToString() ?? "";
                logger.LogInformation("[Chatbot] AIAgent 応答完了 length={Length}", chatbotAnswer.Length);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Chatbot] AIAgent 呼び出し失敗。モック応答を使用します。");
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



