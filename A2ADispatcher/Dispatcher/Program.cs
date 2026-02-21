using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. 依存関係の登録
builder.Services.AddHttpClient();

// K8s クライアントは K8s 環境 (非 Development) でのみ登録
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<Kubernetes>(sp =>
    {
        var config = KubernetesClientConfiguration.InClusterConfig();
        return new Kubernetes(config);
    });
}

// エージェントの名簿（BaseURL をキーにして情報を保持）
var agentCatalog = new ConcurrentDictionary<string, AgentInfo>();

var app = builder.Build();

// 2. 環境に応じてエージェントの探索方法を切り替え
if (app.Environment.IsDevelopment())
{
    // 開発時: 設定ファイルの静的リストからエージェントを登録
    _ = StartStaticAgentsDiscovery(app.Services, agentCatalog, app.Configuration);
}
else
{
    // 本番 (K8s) 時: Pod 監視でエージェントを動的に登録
    _ = StartK8sWatch(app.Services, agentCatalog);
}

// 3. ルーティングエンドポイント
app.MapPost("/agent", async (AgentRequest request, IHttpClientFactory httpClientFactory) =>
{
    // 能力(Capability)に合致するエージェントを検索
    var targetAgent = agentCatalog.Values
        .FirstOrDefault(a => a.Card.GetCapabilityNames()
            .Contains(request.RequiredCapability, StringComparer.OrdinalIgnoreCase));

    if (targetAgent == null)
    {
        return Results.NotFound(new { error = $"能力 '{request.RequiredCapability}' を持つエージェントが見つかりません。登録済み: [{string.Join(", ", agentCatalog.Values.Select(a => $"{a.Card.Name}={string.Join("|", a.Card.GetCapabilityNames())}"))}]" });
    }

    // 発見したエージェントの Endpoint へリクエストを転送
    var httpClient = httpClientFactory.CreateClient();
    var targetUrl = $"{targetAgent.Endpoint}/agent";
    Console.WriteLine($"[Routing] → {targetUrl}");

    var response = await httpClient.PostAsJsonAsync(targetUrl, request);
    var content = await response.Content.ReadFromJsonAsync<object>();

    return Results.Json(content, statusCode: (int)response.StatusCode);
});

app.Run();

// --- ヘルパーメソッド群 ---

// 開発時: appsettings.Development.json の Agents:StaticList からエージェントを登録
async Task StartStaticAgentsDiscovery(
    IServiceProvider services,
    ConcurrentDictionary<string, AgentInfo> catalog,
    IConfiguration config)
{
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();
    var staticAgents = config.GetSection("Agents:StaticList").Get<List<StaticAgentConfig>>() ?? [];

    if (staticAgents.Count == 0)
    {
        Console.WriteLine("[Discovery] Agents:StaticList が設定されていません。appsettings.Development.json を確認してください。");
        return;
    }

    foreach (var agent in staticAgents)
    {
        Console.WriteLine($"[Discovery] 静的エージェントを検索中: {agent.BaseUrl}");
        var card = await FetchAgentCardWithRetry(http, agent.BaseUrl);
        if (card != null)
        {
            catalog[agent.BaseUrl] = new AgentInfo(agent.BaseUrl, card);
            Console.WriteLine($"[Discovery] 登録成功: {card.Name} ({agent.BaseUrl}) - Capabilities: {string.Join(", ", card.GetCapabilityNames())}");
        }
        else
        {
            Console.WriteLine($"[Discovery] 登録失敗: {agent.BaseUrl} - エージェントカードを取得できませんでした。");
        }
    }
}

// 本番 (K8s) 時: Pod 監視でエージェントを動的に登録
async Task StartK8sWatch(IServiceProvider services, ConcurrentDictionary<string, AgentInfo> catalog)
{
    var client = services.GetRequiredService<Kubernetes>();
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient();

    while (true) // 接続切れ・Watch 期限切れ対策のループ
    {
        try
        {
            // ① まず既存 Pod を一括スキャン（Watch 再起動時に既存 Pod を取りこぼさない）
            var existingPods = await client.CoreV1.ListNamespacedPodAsync(
                "default", labelSelector: "app=a2a-agent");

            foreach (var pod in existingPods.Items)
            {
                var podIp = pod.Status?.PodIP;
                if (string.IsNullOrEmpty(podIp) || catalog.ContainsKey(podIp)) continue;
                // Running 状態の Pod のみ対象
                if (pod.Status?.Phase != "Running") continue;

                var baseUrl = $"http://{podIp}:8080";
                Console.WriteLine($"[Discovery] 既存 Pod を発見: {podIp}");
                var card = await FetchAgentCardWithRetry(http, baseUrl);
                if (card != null)
                {
                    catalog[podIp] = new AgentInfo(baseUrl, card);
                    Console.WriteLine($"[Discovery] Registered (initial scan): {card.Name} ({podIp})");
                }
            }

            // ② resourceVersion を記録して Watch を開始（未処理イベントを漏らさない）
            var resourceVersion = existingPods.Metadata.ResourceVersion;
            Console.WriteLine($"[Discovery] Watch 開始 (resourceVersion={resourceVersion})");

            var podWatch = client.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                "default",
                labelSelector: "app=a2a-agent",
                resourceVersion: resourceVersion,
                watch: true);

            await foreach (var (type, item) in podWatch.WatchAsync<V1Pod, V1PodList>())
            {
                var podIp = item.Status?.PodIP;
                if (string.IsNullOrEmpty(podIp)) continue;

                var baseUrl = $"http://{podIp}:8080";

                if (type == WatchEventType.Added || type == WatchEventType.Modified)
                {
                    if (catalog.ContainsKey(podIp)) continue; // 登録済みはスキップ
                    if (item.Status?.Phase != "Running") continue;

                    var card = await FetchAgentCardWithRetry(http, baseUrl);
                    if (card != null)
                    {
                        catalog[podIp] = new AgentInfo(baseUrl, card);
                        Console.WriteLine($"[Discovery] Registered ({type}): {card.Name} ({podIp}) - Capabilities: {string.Join(", ", card.GetCapabilityNames())}");
                    }
                }
                else if (type == WatchEventType.Deleted)
                {
                    catalog.TryRemove(podIp, out _);
                    Console.WriteLine($"[Discovery] Removed: {podIp}");
                }
            }

            Console.WriteLine("[Discovery] Watch ストリームが終了しました。再起動します...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Watch failed: {ex.Message}. Retrying in 5s...");
            await Task.Delay(5000);
        }
    }
}

// エージェントカードを baseUrl から取得（/.well-known/agent.json → /.well-known/agent-card.json の順で試行）
async Task<AgentCard?> FetchAgentCardWithRetry(HttpClient http, string baseUrl)
{
    var candidatePaths = new[]
    {
        "/.well-known/agent.json",
        "/.well-known/agent-card.json"
    };

    for (int i = 0; i < 3; i++)
    {
        foreach (var path in candidatePaths)
        {
            try
            {
                var url = $"{baseUrl.TrimEnd('/')}{path}";
                Console.WriteLine($"[Discovery] Trying {url} ...");
                var card = await http.GetFromJsonAsync<AgentCard>(url);
                if (card != null)
                {
                    Console.WriteLine($"[Discovery] エージェントカード取得成功: {url}");
                    return card;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discovery] 失敗: {ex.Message}");
            }
        }
        await Task.Delay(2000); // 2秒待ってリトライ
    }
    return null;
}

// --- データ構造の定義 ---
public record AgentInfo(string Endpoint, AgentCard Card);
public record StaticAgentConfig(string BaseUrl);
public record AgentRequest(string RequiredCapability, string Message);

public record AgentCapabilitiesInfo(
    [property: JsonPropertyName("streaming")] bool Streaming,
    [property: JsonPropertyName("extensions")] List<AgentExtension>? Extensions);

public record AgentExtension(
    [property: JsonPropertyName("name")] string Name);

public record AgentCard(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("capabilities")] AgentCapabilitiesInfo? Capabilities)
{
    /// <summary>
    /// ケーパビリティ名の一覧を返す。
    /// Extensions に名前がある場合はそれを使用し、なければエージェント名をフォールバックとして返す。
    /// </summary>
    public IEnumerable<string> GetCapabilityNames()
    {
        var result = new List<string>();
        if (Capabilities?.Streaming == true)
            result.Add("streaming");
        if (Capabilities?.Extensions != null)
            result.AddRange(Capabilities.Extensions.Select(e => e.Name));
        // エクステンションが空の場合、エージェント名をフォールバックとして使用
        if (result.Count == 0 && !string.IsNullOrEmpty(Name))
            result.Add(Name);
        return result;
    }
}