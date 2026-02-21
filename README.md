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

Dispatcher は Kubernetes 上のエージェント Pod を自動で検出し、`requiredCapability` に合致するエージェントへリクエストをルーティングします。

### 1. コンテナイメージのビルド

```bash
cd A2ADispatcher/Dispatcher
docker build -t a2a-dispatcher:latest .
```

### 2. Kubernetes へのデプロイ

```bash
cd A2ADispatcher
kubectl apply -f infrastructure.yaml
```

デプロイ状況を確認します。

```bash
kubectl get pods -l app=a2a-dispatcher
kubectl get svc a2a-dispatcher-svc
```

### 3. NodePort の確認

```bash
kubectl get svc a2a-dispatcher-svc
# NAME                  TYPE       CLUSTER-IP      EXTERNAL-IP   PORT(S)        AGE
# a2a-dispatcher-svc   NodePort   10.96.x.x       <none>        80:3xxxx/TCP   ...
```

`PORT(S)` 列の `80:3xxxx` の右側の番号 (例: `30080`) が NodePort です。

### 4. リクエストの送信

`POST /agent` エンドポイントに JSON ボディを送ります。

```bash
curl -X POST http://localhost:<NodePort>/agent \
  -H "Content-Type: application/json" \
  -d '{
    "requiredCapability": "weather",
    "message": "今日の東京の天気は？"
  }'
```

#### リクエストボディの説明

| フィールド | 型 | 説明 |
|---|---|---|
| `requiredCapability` | string | 処理させたいエージェントの能力名 (例: `"weather"`) |
| `message` | string | エージェントに送るメッセージ本文 |

#### レスポンス例

```json
{
  "result": "現在の東京は晴れです。"
}
```

能力を持つエージェントが見つからない場合は `404` が返ります。

```json
{
  "error": "能力 'weather' を持つエージェントが見つかりません。"
}
```

### 5. ローカル開発時 (Kubernetes なし)

`dotnet run` で直接起動する場合は `http://localhost:5073` を使います。  
※ Kubernetes クライアントが InClusterConfig を要求するため、ローカル実行では K8s 接続エラーが出ますが、エンドポイント自体の動作確認はできます。

```bash
cd A2ADispatcher/Dispatcher
dotnet run
```

```bash
curl -X POST http://localhost:5073/agent \
  -H "Content-Type: application/json" \
  -d '{"requiredCapability": "weather", "message": "今日の天気は？"}'
```

### 6. ログの確認

```bash
kubectl logs -f deployment/a2a-dispatcher
# [Discovery] Registered: WeatherAgent (10.x.x.x) - Capabilities: weather
# [Discovery] Removed: 10.x.x.x
```

---

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

## wip

```bash

kubectl rollout restart deployment a2a-agents
kubectl rollout restart deployment a2a-dispatcher

kubectl get pods 
kubectl port-forward a2a-dispatcher-594c65c8cd-xmdlt  7777:8080

kubectl logs -f a2a-dispatcher-58c976cf69-2k52j
```

