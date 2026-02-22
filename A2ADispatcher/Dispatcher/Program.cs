using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using k8s;
using k8s.Models;

var builder = WebApplication.CreateBuilder(args);

// 1. 依存関係の登録
builder.Services.AddHttpClient();

// エージェントの名簿（BaseURL をキーにして情報を保持）をシングルトンとして登録
builder.Services.AddSingleton(new ConcurrentDictionary<string, AgentInfo>());

// K8s クライアントは K8s 環境 (非 Development) でのみ登録
// 環境に応じてエージェントの探索サービスを切り替え
if (!builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<Kubernetes>(sp =>
    {
        var config = KubernetesClientConfiguration.InClusterConfig();
        return new Kubernetes(config);
    });
    // 本番 (K8s) 時: Pod 監視でエージェントを動的に登録
    builder.Services.AddHostedService<K8sAgentDiscoveryService>();
}
else
{
    // 開発時: 設定ファイルの静的リストからエージェントを登録
    builder.Services.AddHostedService<StaticAgentDiscoveryService>();
}

var app = builder.Build();

// 2. ルーティングエンドポイント
app.MapPost("/agent", async (AgentRequest request, IHttpClientFactory httpClientFactory, ConcurrentDictionary<string, AgentInfo> agentCatalog) =>
{
    // 能力(Capability)に合致するエージェントを検索
    var targetAgent = agentCatalog.Values
        .FirstOrDefault(a => a.Card.GetCapabilityNames()
            .Contains(request.RequiredCapability, StringComparer.OrdinalIgnoreCase));

    if (targetAgent == null)
    {
        return Results.NotFound(new
        {
            error = $"能力 '{request.RequiredCapability}' を持つエージェントが見つかりません。",
            registered = agentCatalog.Values.Select(a => new
            {
                name = a.Card.Name,
                capabilities = a.Card.GetCapabilityNames()
            })
        });
    }

    // 発見したエージェントの Endpoint へ A2A JSON-RPC message/send を転送
    var httpClient = httpClientFactory.CreateClient();
    var targetUrl = $"{targetAgent.Endpoint}/agent";
    Console.WriteLine($"[Routing] {request.RequiredCapability} → {targetUrl}");

    // A2A SDK が期待する JSON-RPC リクエストを組み立てる
    var rpcRequest = new A2AJsonRpcRequest
    {
        Id = Guid.NewGuid().ToString(),
        Method = "message/send",
        Params = new A2AMessageSendParams
        {
            Message = new A2AMessage
            {
                Role = "user",
                MessageId = Guid.NewGuid().ToString(),
                Parts = [new A2ATextPart { Text = request.Message }]
            }
        }
    };

    var response = await httpClient.PostAsJsonAsync(targetUrl, rpcRequest);
    var rpcResponse = await response.Content.ReadFromJsonAsync<A2AJsonRpcResponse>();

    if (rpcResponse?.Error != null)
    {
        return Results.Problem(rpcResponse.Error.Message, statusCode: 502);
    }

    // レスポンスからテキストを取り出して返す
    var replyText = rpcResponse?.Result?.Parts
        ?.OfType<System.Text.Json.JsonElement>()
        .Select(p =>
        {
            p.TryGetProperty("text", out var t);
            return t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
        })
        .FirstOrDefault(t => t != null)
        ?? rpcResponse?.Result?.ToString();

    Console.WriteLine($"[Routing] ← {targetAgent.Card.Name}: {replyText}");

    return Results.Ok(new
    {
        agent = targetAgent.Card.Name,
        endpoint = targetUrl,
        reply = replyText,
        rawResult = rpcResponse?.Result
    });
});

app.Run();

// --- BackgroundService 実装 ---

// 開発時: appsettings.Development.json の Agents:StaticList からエージェントを登録
public class StaticAgentDiscoveryService(
    IHttpClientFactory httpClientFactory,
    ConcurrentDictionary<string, AgentInfo> catalog,
    IConfiguration config,
    ILogger<StaticAgentDiscoveryService> logger) : BackgroundService
{
    private const int MaxRetryAttempts = 3;
    private const int RetryDelayMilliseconds = 2000;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpClientFactory.CreateClient();
        var staticAgents = config.GetSection("Agents:StaticList").Get<List<StaticAgentConfig>>() ?? [];

        if (staticAgents.Count == 0)
        {
            logger.LogWarning("[Discovery] Agents:StaticList が設定されていません。appsettings.Development.json を確認してください。");
            return;
        }

        foreach (var agent in staticAgents)
        {
            if (stoppingToken.IsCancellationRequested) break;

            logger.LogInformation("[Discovery] 静的エージェントを検索中: {BaseUrl}", agent.BaseUrl);
            var card = await FetchAgentCardWithRetry(http, agent.BaseUrl, stoppingToken);
            if (card != null)
            {
                catalog[agent.BaseUrl] = new AgentInfo(agent.BaseUrl, card);
                logger.LogInformation("[Discovery] 登録成功: {Name} ({BaseUrl}) - Capabilities: {Capabilities}",
                    card.Name, agent.BaseUrl, string.Join(", ", card.GetCapabilityNames()));
            }
            else
            {
                logger.LogWarning("[Discovery] 登録失敗: {BaseUrl} - エージェントカードを取得できませんでした。", agent.BaseUrl);
            }
        }
    }

    // エージェントカードを baseUrl から取得（/.well-known/agent.json → /.well-known/agent-card.json の順で試行）
    private async Task<AgentCard?> FetchAgentCardWithRetry(HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        var candidatePaths = new[]
        {
            "/.well-known/agent.json",
            "/.well-known/agent-card.json"
        };

        for (int i = 0; i < MaxRetryAttempts; i++)
        {
            foreach (var path in candidatePaths)
            {
                try
                {
                    var url = $"{baseUrl.TrimEnd('/')}{path}";
                    logger.LogInformation("[Discovery] Trying {Url} ...", url);
                    var card = await http.GetFromJsonAsync<AgentCard>(url, cancellationToken);
                    if (card != null)
                    {
                        logger.LogInformation("[Discovery] エージェントカード取得成功: {Url}", url);
                        return card;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("[Discovery] 失敗: {Message}", ex.Message);
                }
            }
            await Task.Delay(RetryDelayMilliseconds, cancellationToken); // 2秒待ってリトライ
        }
        return null;
    }
}

// 本番 (K8s) 時: Pod 監視でエージェントを動的に登録
public class K8sAgentDiscoveryService(
    Kubernetes k8sClient,
    IHttpClientFactory httpClientFactory,
    ConcurrentDictionary<string, AgentInfo> catalog,
    ILogger<K8sAgentDiscoveryService> logger) : BackgroundService
{
    private const int MaxRetryAttempts = 3;
    private const int RetryDelayMilliseconds = 2000;
    private const int WatchRetryDelayMilliseconds = 5000;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var http = httpClientFactory.CreateClient();

        while (!stoppingToken.IsCancellationRequested) // 接続切れ・Watch 期限切れ対策のループ
        {
            try
            {
                // ① まず既存 Pod を一括スキャン（Watch 再起動時に既存 Pod を取りこぼさない）
                var existingPods = await k8sClient.CoreV1.ListNamespacedPodAsync(
                    "default", labelSelector: "app=a2a-agent", cancellationToken: stoppingToken);

                foreach (var pod in existingPods.Items)
                {
                    if (stoppingToken.IsCancellationRequested) return;

                    var podIp = pod.Status?.PodIP;
                    if (string.IsNullOrEmpty(podIp) || catalog.ContainsKey(podIp)) continue;
                    // Running 状態の Pod のみ対象
                    if (pod.Status?.Phase != "Running") continue;

                    var baseUrl = $"http://{podIp}:8080";
                    logger.LogInformation("[Discovery] 既存 Pod を発見: {PodIp}", podIp);
                    var card = await FetchAgentCardWithRetry(http, baseUrl, stoppingToken);
                    if (card != null)
                    {
                        catalog[podIp] = new AgentInfo(baseUrl, card);
                        logger.LogInformation("[Discovery] Registered (initial scan): {Name} ({PodIp})", card.Name, podIp);
                    }
                }

                // ② resourceVersion を記録して Watch を開始（未処理イベントを漏らさない）
                var resourceVersion = existingPods.Metadata.ResourceVersion;
                logger.LogInformation("[Discovery] Watch 開始 (resourceVersion={ResourceVersion})", resourceVersion);

                var podWatch = k8sClient.CoreV1.ListNamespacedPodWithHttpMessagesAsync(
                    "default",
                    labelSelector: "app=a2a-agent",
                    resourceVersion: resourceVersion,
                    watch: true);

#pragma warning disable CS0618 // WatchAsync(Task<...>) は将来非推奨予定だが、このバージョンでは唯一の利用可能なオーバーロード
                await foreach (var (type, item) in podWatch.WatchAsync<V1Pod, V1PodList>(
                    onError: ex => logger.LogError(ex, "[Discovery] Watch エラー"),
                    cancellationToken: stoppingToken))
#pragma warning restore CS0618
                {
                    var podIp = item.Status?.PodIP;
                    if (string.IsNullOrEmpty(podIp)) continue;

                    var baseUrl = $"http://{podIp}:8080";

                    if (type == WatchEventType.Added || type == WatchEventType.Modified)
                    {
                        if (catalog.ContainsKey(podIp)) continue; // 登録済みはスキップ
                        if (item.Status?.Phase != "Running") continue;

                        var card = await FetchAgentCardWithRetry(http, baseUrl, stoppingToken);
                        if (card != null)
                        {
                            catalog[podIp] = new AgentInfo(baseUrl, card);
                            logger.LogInformation("[Discovery] Registered ({Type}): {Name} ({PodIp}) - Capabilities: {Capabilities}",
                                type, card.Name, podIp, string.Join(", ", card.GetCapabilityNames()));
                        }
                    }
                    else if (type == WatchEventType.Deleted)
                    {
                        catalog.TryRemove(podIp, out _);
                        logger.LogInformation("[Discovery] Removed: {PodIp}", podIp);
                    }
                }

                logger.LogInformation("[Discovery] Watch ストリームが終了しました。再起動します...");
            }
            catch (OperationCanceledException)
            {
                // ホスト停止時の正常終了
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Error] Watch failed. Retrying in 5s...");
                await Task.Delay(WatchRetryDelayMilliseconds, stoppingToken);
            }
        }
    }

    // エージェントカードを baseUrl から取得（/.well-known/agent.json → /.well-known/agent-card.json の順で試行）
    private async Task<AgentCard?> FetchAgentCardWithRetry(HttpClient http, string baseUrl, CancellationToken cancellationToken)
    {
        var candidatePaths = new[]
        {
            "/.well-known/agent.json",
            "/.well-known/agent-card.json"
        };

        for (int i = 0; i < MaxRetryAttempts; i++)
        {
            foreach (var path in candidatePaths)
            {
                try
                {
                    var url = $"{baseUrl.TrimEnd('/')}{path}";
                    logger.LogInformation("[Discovery] Trying {Url} ...", url);
                    var card = await http.GetFromJsonAsync<AgentCard>(url, cancellationToken);
                    if (card != null)
                    {
                        logger.LogInformation("[Discovery] エージェントカード取得成功: {Url}", url);
                        return card;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("[Discovery] 失敗: {Message}", ex.Message);
                }
            }
            await Task.Delay(RetryDelayMilliseconds, cancellationToken); // 2秒待ってリトライ
        }
        return null;
    }
}

// --- データ構造の定義 ---
public record AgentInfo(string Endpoint, AgentCard Card);
public record StaticAgentConfig(string BaseUrl);
public record AgentRequest(string RequiredCapability, string Message);

// A2A JSON-RPC リクエスト
public class A2AJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public string Id { get; set; } = "";
    [JsonPropertyName("method")]  public string Method { get; set; } = "";
    [JsonPropertyName("params")]  public A2AMessageSendParams? Params { get; set; }
}

public class A2AMessageSendParams
{
    [JsonPropertyName("message")] public A2AMessage? Message { get; set; }
}

public class A2AMessage
{
    [JsonPropertyName("kind")]      public string Kind { get; set; } = "message";
    [JsonPropertyName("role")]      public string Role { get; set; } = "user";
    [JsonPropertyName("messageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("parts")]     public List<A2ATextPart> Parts { get; set; } = [];
}

public class A2ATextPart
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

// A2A JSON-RPC レスポンス
public class A2AJsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; set; }
    [JsonPropertyName("id")]      public string? Id { get; set; }
    [JsonPropertyName("result")]  public A2AMessageResult? Result { get; set; }
    [JsonPropertyName("error")]   public A2AJsonRpcError? Error { get; set; }
}

public class A2AMessageResult
{
    [JsonPropertyName("role")]      public string? Role { get; set; }
    [JsonPropertyName("messageId")] public string? MessageId { get; set; }
    [JsonPropertyName("parts")]     public List<System.Text.Json.JsonElement>? Parts { get; set; }
    public override string ToString()
        => Parts != null ? string.Join("", Parts.Select(p =>
            p.TryGetProperty("text", out var t) ? t.GetString() : "")) : "";
}

public class A2AJsonRpcError
{
    [JsonPropertyName("code")]    public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

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