using System.Collections.Concurrent;
using System.Net.Http.Json;
using k8s;
using k8s.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. 依存関係の登録
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Kubernetes>(sp => {
    var config = KubernetesClientConfiguration.InClusterConfig();
    return new Kubernetes(config);
});

// エージェントの名簿（IPをキーにして情報を保持）
var agentCatalog = new ConcurrentDictionary<string, AgentInfo>();

var app = builder.Build();

// 2. K8s 監視ロジックをバックグラウンドで開始
// (awaitせずに開始することで、Webサーバの起動を妨げない)
_ = StartK8sWatch(app.Services, agentCatalog);

// 3. ルーティングエンドポイント
app.MapPost("/agent", async (HttpContext context, AgentRequest request, HttpClient httpClient) =>
{
    // 能力(Capability)に合致するエージェントを検索
    var targetAgent = agentCatalog.Values
        .FirstOrDefault(a => a.Card.Capabilities.Contains(request.RequiredCapability));

    if (targetAgent == null)
    {
        return Results.NotFound(new { error = $"能力 '{request.RequiredCapability}' を持つエージェントが見つかりません。" });
    }

    // 発見したエージェントの Pod IP へリクエストを転送
    // (ここでは簡易的に HttpClient を使用。本気なら YARP を使うのが◎)
    var targetUrl = $"http://{targetAgent.Ip}:8080/agent";
    
    // ボディを再送するために中身を読み取る
    var response = await httpClient.PostAsJsonAsync(targetUrl, request);
    var content = await response.Content.ReadFromJsonAsync<object>();

    return Results.Json(content, statusCode: (int)response.StatusCode);
});

app.Run();

// --- ヘルパーメソッド群 ---

async Task StartK8sWatch(IServiceProvider services, ConcurrentDictionary<string, AgentInfo> catalog)
{
    var client = services.GetRequiredService<Kubernetes>();
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();

    while (true) // 接続切れ対策のループ
    {
        try
        {
            var podWatch = client.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                "default", labelSelector: "app=a2a-agent", watch: true);

            await foreach (var (type, item) in podWatch.WatchAsync<V1Pod, V1PodList>())
            {
                var podIp = item.Status.PodIP;
                if (string.IsNullOrEmpty(podIp)) continue;

                if (type == WatchEventType.Added)
                {
                    // カードを取得して名簿に登録
                    var card = await FetchAgentCardWithRetry(http, podIp);
                    if (card != null)
                    {
                        catalog[podIp] = new AgentInfo(podIp, card);
                        Console.WriteLine($"[Discovery] Registered: {card.Name} ({podIp}) - Capabilities: {string.Join(",", card.Capabilities)}");
                    }
                }
                else if (type == WatchEventType.Deleted)
                {
                    catalog.TryRemove(podIp, out _);
                    Console.WriteLine($"[Discovery] Removed: {podIp}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Watch failed: {ex.Message}. Retrying in 5s...");
            await Task.Delay(5000);
        }
    }
}

async Task<AgentCard?> FetchAgentCardWithRetry(HttpClient http, string ip)
{
    // Pod起動直後は接続できないことがあるので3回リトライ
    for (int i = 0; i < 3; i++)
    {
        try
        {
            // 前回の教訓：正しいパスを叩く
            return await http.GetFromJsonAsync<AgentCard>($"http://{ip}:8080/.well-known/agent-card.json");
        }
        catch
        {
            await Task.Delay(2000); // 2秒待ってリトライ
        }
    }
    return null;
}

// --- データ構造の定義 ---
public record AgentInfo(string Ip, AgentCard Card);
public record AgentCard(string Name, string[] Capabilities);
public record AgentRequest(string RequiredCapability, string Message);