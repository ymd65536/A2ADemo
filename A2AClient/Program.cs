using A2A;

// 1. サーバのログに合わせてポート番号を 5162、プロトコルを http に変更
var serverUri = new Uri("http://localhost:5162/agent");

// 2. エージェントカードの解決
var cardResolver = new A2ACardResolver(serverUri);
var agentCard = await cardResolver.GetAgentCardAsync();

// 3. クライアントの作成（カードに記載されたURLを使用）
var client = new A2AClient(new Uri(agentCard.Url));

// 4. メッセージの送信
var response = await client.SendMessageAsync(new MessageSendParams
{
    Message = new AgentMessage
    {
        Role = MessageRole.User,
        Parts = [new TextPart { Text = "こんにちは！" }]
    }
});

// 5. 応答の表示（型チェックを行ってからPartsにアクセス）
if (response is AgentMessage agentMessage)
{
    var replyText = agentMessage.Parts.OfType<TextPart>().FirstOrDefault()?.Text;
    Console.WriteLine($"エージェントからの返答: {replyText}");
}
