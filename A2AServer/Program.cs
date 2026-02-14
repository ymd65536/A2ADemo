using A2A;
using A2A.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

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
    public void Attach(ITaskManager taskManager)
    {
        // メッセージ受信時の処理を登録
        taskManager.OnMessageReceived = ProcessMessageAsync;
        // エージェント情報の問い合わせに対する応答を登録
        taskManager.OnAgentCardQuery = GetAgentCardAsync;
    }

    private Task<A2AResponse> ProcessMessageAsync(MessageSendParams messageParams, CancellationToken ct)
    {
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
            Capabilities = new AgentCapabilities { Streaming = false }
        });
    }
}
