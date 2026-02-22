## A2A Demo

A2Aで動かすまえにJSON RPCとKubernetesを使用して通信する方法を説明します。
そのあと、Microsoft Agent FrameworkとA2Aで動かす方法を説明します。

## Step1: JSON RPCとKubernetesを使用して通信

このステップを完了するとKubernetesクラスターとJSON RPCを使用して通信できます。

まずはコンテナイメージをビルドします。WeatherAgentとOrchestratorの両方をビルドしてください。

```bash
cd WeatherAgent
docker build -t a2a-weather:net10 .
cd ../Orchestrator
docker build -t a2a-orchestrator:net10 .
```

次に、kubectlを使用してクラスターにapplyします。
k8s/a2a-deploy.yamlを使用して、両方のコンテナイメージをクラスターにデプロイしてください。

```bash
cd ..
kubectl apply -f k8s/a2a-deploy.yaml
```

クラスターが起動したら、Orchestratorのサービスを確認します。

```bash
curl "http://localhost:30001/ask?tool=get_weather"
# [.NET 10 Orchestrator] Result: {"jsonrpc":"2.0","result":"現在の東京は .NET 10 のように爽やかな快晴です。","id":"179da644-271c-462f-aa3f-04e939b8e780"}%  
```

## Step2: A2AをKubernetesで動かす

まずは、A2A ServerとA2A Clientの両方をビルドしてください。

A2A Serverをビルドします。

```bash
cd A2AServer
docker build -t a2a-a2a-server:net10 .
```

次にA2A Clientをビルドします。

```bash
cd A2AClient
docker build -t a2a-orch-a2a-client:net10 .
```

KubernetesクラスターにA2A ServerとA2A Clientをデプロイします。

```bash
cd k8s
kubectl apply -f a2a-client-server.yaml
```

A2A Clientが起動したら、Orchestratorのサービスを確認します。

```bash
curl -G "http://localhost:30001/ask" --data-urlencode "text=こんにちは" -v
```

## Prometheus and Grafana

つぎに、PrometheusとGrafanaを使用してクラスターのモニタリングを行います。
べつのターミナルで、以下のコマンドを実行してPrometheusとGrafanaをクラスターにデプロイしてください。

まずは、k8sディレクトリに移動します。

```bash
cd k8s
```

Prometheusをデプロイします。

```bash
kubectl apply -f k8s/prometheus.yaml
```

つぎに、Grafanaをデプロイします。

```bash
kubectl apply -f k8s/grafana.yaml
```

## Step3: Dispatcher を使ってエージェントにリクエストを送る

Dispatcher は Kubernetes 上のエージェント Pod を自動で検出し、`requiredCapability` に合致するエージェントへ A2A プロトコル (JSON-RPC `message/send`) でリクエストをルーティングします。

### 仕組み

```
curl POST /agent
  ↓
Dispatcher
  ├─ [起動時] app=a2a-agent ラベルの Pod を K8s Watch で監視
  │           → /.well-known/agent-card.json を取得してカタログに登録
  └─ [受信時] requiredCapability に合致するエージェントを検索
              → A2A JSON-RPC (message/send) でその Pod IP へ転送
                ↓
              A2AServer Pod
                ↓
              レスポンスを返す
```

新しいエージェントを追加するには、Pod ラベルに `app: a2a-agent` を付けて `kubectl apply` するだけで Dispatcher が自動発見します。

### 1. コンテナイメージのビルド

A2AServer と Dispatcher の両方をビルドします。

```bash
cd ./A2ADispatcher/SimpleAgent
docker build -t a2a-simple-agent:net10 .

cd ../Dispatcher
docker build -t a2a-dispatcher:latest .
```

### 2. Kubernetes へのデプロイ

`infrastructure.yaml` には ServiceAccount / RBAC / Dispatcher / A2AServer がすべて含まれています。

```bash
cd A2ADispatcher
kubectl apply -f infrastructure.yaml
```

Pod の起動を確認します。

```bash
kubectl get pods
# NAME                              READY   STATUS    RESTARTS   AGE
# a2a-dispatcher-xxxxxxxxxx-xxxxx   1/1     Running   0          ...
# a2a-server-xxxxxxxxxx-xxxxx       1/1     Running   0          ...
```

Dispatcher のログで A2AServer が自動登録されていることを確認します。

```bash
kubectl logs -f deployment/a2a-dispatcher
# [Discovery] 既存 Pod を発見: 10.42.x.x
# [Discovery] Trying http://10.42.x.x:8080/.well-known/agent.json ...
# [Discovery] エージェントカード取得成功: http://10.42.x.x:8080/.well-known/agent-card.json
# [Discovery] Registered (initial scan): サンプル .NET エージェント (10.42.x.x)
# [Discovery] Watch 開始 (resourceVersion=xxxxx)
```

### 3. リクエストの送信

Dispatcher の NodePort は `30010` に固定されています。
`POST /agent` エンドポイントに JSON ボディを送ります。

```bash
curl -X POST http://localhost:30010/agent \
  -H "Content-Type: application/json" \
  -d '{
    "requiredCapability": "サンプル .NET エージェント",
    "message": "こんにちは"
  }'
```

#### リクエストボディの説明

| フィールド | 型 | 説明 |
|---|---|---|
| `requiredCapability` | string | エージェントカードの `capabilities.extensions[].name`、または未設定の場合はエージェント名 |
| `message` | string | エージェントに送るメッセージ本文 |

#### レスポンス例

```json
{
  "agent": "サンプル .NET エージェント",
  "endpoint": "http://10.42.x.x:8080/agent",
  "reply": "[A2A応答] あなたは「こんにちは」と言いましたね。",
  "rawResult": {
    "role": "agent",
    "messageId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
    "parts": [
      { "kind": "text", "text": "[A2A応答] あなたは「こんにちは」と言いましたね。" }
    ]
  }
}
```

能力を持つエージェントが見つからない場合は `404` が返ります。

```json
{
  "error": "能力 'xxx' を持つエージェントが見つかりません。",
  "registered": [
    { "name": "サンプル .NET エージェント", "capabilities": ["サンプル .NET エージェント"] }
  ]
}
```

### 4. ローカル開発時 (Kubernetes なし)

`appsettings.Development.json` の `Agents:StaticList` に A2AServer の URL を指定することで、K8s なしでも動作確認できます。

```json
{
  "Agents": {
    "StaticList": [
      { "BaseUrl": "http://localhost:5162" }
    ]
  }
}
```

A2AServer を起動してから Dispatcher を起動します。

```bash
# ターミナル1
cd A2AServer
dotnet run
# → http://localhost:5162 で起動

# ターミナル2
cd A2ADispatcher/Dispatcher
dotnet run
# → http://localhost:5073 で起動
# [Discovery] 静的エージェントを検索中: http://localhost:5162
# [Discovery] 登録成功: サンプル .NET エージェント (http://localhost:5162)
```

```bash
curl -X POST http://localhost:5073/agent \
  -H "Content-Type: application/json" \
  -d '{"requiredCapability": "サンプル .NET エージェント", "message": "こんにちは"}'
```

### 5. 新しいエージェントの追加

`app: a2a-agent` ラベルを付けた Deployment を `kubectl apply` するだけで Dispatcher が自動発見します。

```yaml
template:
  metadata:
    labels:
      app: a2a-agent  # ← このラベルが Discovery の唯一の条件
```

エージェント側のコードで `capabilities.extensions` に能力名を宣言すると、その名前で検索できるようになります。

### 6.Aspire Dashboardを起動

まずはk8sでAspire Dashboardを起動します。

```bash
kubectl apply -f A2ADispatcher/aspire-dashboard.yaml
```

起動したら、ブラウザで `http://localhost:30080` にアクセスしてください。

試しに、Dispatcher にリクエストを送ってみます。

```bash
curl -X POST http://localhost:30010/agent \
  -H "Content-Type: application/json" \
  -d '{
    "requiredCapability": "サンプル .NET エージェント",
    "message": "こんにちは"
  }'
```

### 7. Agent Card の取得

`simple-agent-svc` は ClusterIP のため、`kubectl port-forward` で一時的に転送してから取得します。

```bash
kubectl port-forward svc/simple-agent-svc 8088:80
```

別のターミナルで以下のコマンドを実行して、エージェントカードを取得します。

```
curl -s http://localhost:8088/.well-known/agent-card.json | jq .
```

取得後はポートフォワードを終了します。

```bash
pkill -f "port-forward svc/simple-agent-svc"
```

#### レスポンス例

```json
{
  "name": "サンプル .NET エージェント",
  "description": "A2Aプロトコルで通信するデモ用エージェントです。",
  "url": "http://localhost:8088/agent",
  "iconUrl": null,
  "provider": null,
  "version": "",
  "protocolVersion": "0.3.0",
  "documentationUrl": null,
  "capabilities": {
    "streaming": false,
    "pushNotifications": false,
    "stateTransitionHistory": false,
    "extensions": []
  },
  "securitySchemes": null,
  "security": null,
  "defaultInputModes": [
    "text"
  ],
  "defaultOutputModes": [
    "text"
  ],
  "skills": [],
  "supportsAuthenticatedExtendedCard": false,
  "additionalInterfaces": [],
  "preferredTransport": "JSONRPC",
  "signatures": null
}
```

### AgentCardViewerを起動する

AgentCardViewer は登録済みエージェントのカード情報を一覧表示する Blazor フロントエンドです。

#### コンテナイメージのビルド

```bash
cd A2ADispatcher/AgentCardViewer
docker build -t a2a-agent-card-viewer:latest .
```

#### Kubernetes へのデプロイ

`infrastructure.yaml` に含まれているため、すでにデプロイ済みの場合は apply のみで反映されます。

```bash
cd A2ADispatcher
kubectl apply -f infrastructure.yaml
```

#### アクセス

ブラウザで `http://localhost:30020` を開くと、登録済みエージェントのカードが一覧表示されます。

### エコーエージェントの動作確認

`infrastructure.yaml` には `EchoAgent` も含まれています。デプロイ後、Dispatcher に自動登録されていることを確認します。

```bash
curl -s http://localhost:30010/agents | jq '[.[] | {name}]'
# [
#   { "name": "エコーエージェント" },
#   { "name": "サンプル .NET エージェント" }
# ]
```

エコーエージェントにメッセージを送ります。Dispatcher経由で、エコーエージェントが受け取ったメッセージをそのまま返します。

```bash
curl -X POST http://localhost:30010/agent \
  -H "Content-Type: application/json" \
  -d '{
    "requiredCapability": "エコーエージェント",
    "message": "こんにちは"
  }'
```

#### レスポンス例

```json
{
  "agent": "エコーエージェント",
  "endpoint": "http://10.42.0.32:8080/agent",
  "reply": "[Echo] こんにちは",
  "rawResult": {
    "role": "agent",
    "messageId": "199d8c98-9d9a-49fc-a484-5c91b4d767b3",
    "parts": [
      { "kind": "text", "text": "[Echo] こんにちは" }
    ]
  }
}
```

#### エージェント一覧 (`/agents`) のレスポンス例

```bash
curl -s http://localhost:30010/agents | jq .
```

```json
[
  {
    "endpoint": "http://10.42.0.32:8080",
    "name": "エコーエージェント",
    "description": "受け取ったメッセージをそのまま返すエコーエージェントです。",
    "capabilities": ["エコーエージェント"],
    "streaming": false,
    "extensions": []
  },
  {
    "endpoint": "http://10.42.0.24:8080",
    "name": "サンプル .NET エージェント",
    "description": "A2Aプロトコルで通信するデモ用エージェントです。",
    "capabilities": ["サンプル .NET エージェント"],
    "streaming": false,
    "extensions": []
  }
]
```

AgentCardViewer (`http://localhost:30020`) を更新すると、2つのエージェントカードが表示されます。

### 片付け

デプロイしたリソースをすべて削除します。

```bash
cd A2ADispatcher
kubectl delete -f infrastructure.yaml
```

Aspire Dashboard もデプロイしている場合は合わせて削除します。

```bash
kubectl delete -f aspire-dashboard.yaml
```

削除後、Pod が残っていないことを確認します。

```bash
kubectl get pods
```

## memo: kubectl

```bash
kubectl get svc
```

```bash
kubectl get pods
```

```bash
kubectl rollout restart deployment a2a-server
```

```bash
kubectl rollout restart deployment orchestrator-a2a-client
```

```bash
kubectl logs orchestrator-a2a-client-6b6448f696-mrxck
```

## memo

`OrchestratorAgent`は`AgentSample`のOrchestrator、`WeatherAgent`は`AgentSample`のWeatherAgentです。
どちらもJSON RPCを使用して通信します。
