using A2A;
using A2A.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// エージェントへの接続設定
var serverUri = new Uri("http://a2a-server-svc/agent");
var cardResolver = new A2ACardResolver(serverUri);

// 外部から「http://(PodのIP):8080/ask?text=こんにちは」のように叩けるようにする
app.MapGet("/ask", async (string text) =>
{
    // 毎回カードを解決して、最新のエージェントURLを取得
    var agentCard = await cardResolver.GetAgentCardAsync();
    var client = new A2AClient(new Uri(agentCard.Url));

    // エージェントにメッセージを送信
    var response = await client.SendMessageAsync(new MessageSendParams
    {
        Message = new AgentMessage
        {
            Role = MessageRole.User,
            Parts = [new TextPart { Text = text }]
        }
    });

    if (response is AgentMessage agentMessage)
    {
        return agentMessage.Parts.OfType<TextPart>().FirstOrDefault()?.Text ?? "返答なし";
    }
    return "エラー";
});

// app.Run() により、プロセスは終了せずリクエストを待ち続ける
app.Run();