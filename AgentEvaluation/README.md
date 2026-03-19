# Multi-Agent Evaluation System

## Overview

複数の評価エージェントを作成してKubernetes上で動かすためのシステムです。評価エージェントは、Azure AI Evaluation SDKを使用して、Microsoft Agent Frameworkをベースに構築されます。これらのエージェントは、A2A dotnetを利用して、様々なタスクやシナリオに対して評価を行います。また、個々のエージェントはKubernetes上のサービス単位で独立して動作し、他のシステムからA2Aで呼び出すことができます。

## SDK

- Azure AI Evaluation SDK
- Microsoft Agent Framework
- A2A dotnet

## System Architecture

3つのエージェントがKubernetes上で動作し、各エージェントは独立したサービスとして提供されます。これらのエージェントは、A2A dotnetを通じて呼び出され、評価タスクを実行します。

1. **Violence Evaluator**: 暴力的なコンテンツを評価するエージェント。
2. **Sexual Evaluator**: 性的なコンテンツを評価するエージェント。
3. **chatbot**: チャットボットエージェント。ユーザーとの対話を通じて、様々なタスクやシナリオに対して評価を行います。応答内容はいったん各Evaluatorを呼び出してから返す形であり、Evaluatorを呼び出すかどうかはユーザーからの質問内容によって判断されます。

3つのエージェントとは別にA2AのクライアントもKubernetes上で動作し、これらのエージェントを呼び出すことができるフロントエンドとして機能します。

### A2A 接続フロー

```
Client
  └─ POST /agent (A2A message/send)
       └─ Chatbot (:30200)
            ├─ A2A → ViolenceEvaluator (violence-evaluator-svc:80)
            └─ A2A → SexualEvaluator   (sexual-evaluator-svc:80)
```

Chatbot はユーザーメッセージを受信し、センシティブなキーワードを含む場合に限り ViolenceEvaluator と SexualEvaluator を**並列**で A2A 呼び出しします。評価結果に問題がなければ通常の応答を、フラグが立った場合は安全上の警告を返します。

## ディレクトリ構成

```
AgentEvaluation/
├── README.md
├── infrastructure.yaml          # Kubernetes マニフェスト (Namespace / Deployment / Service / Secret)
├── ViolenceEvaluator/           # エージェント 1
│   ├── ViolenceEvaluator.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── dockerfile
│   └── Properties/launchSettings.json
├── SexualEvaluator/             # エージェント 2
│   ├── SexualEvaluator.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── dockerfile
│   └── Properties/launchSettings.json
└── Chatbot/                     # エージェント 3 (オーケストレーター)
    ├── Chatbot.csproj
    ├── Program.cs
    ├── appsettings.json
    ├── appsettings.Development.json
    ├── dockerfile
    └── Properties/launchSettings.json
```

## エージェント詳細

### Violence Evaluator

| 項目 | 値 |
|---|---|
| A2A エンドポイント | `POST /agent` |
| AgentCard | `GET /.well-known/agent-card.json` |
| ローカルポート (開発時) | `5200` |
| K8s Service | `violence-evaluator-svc:80` |

Azure AI Content Safety の `Violence` カテゴリでテキストを評価します。  
`AzureContentSafety:Endpoint` / `AzureContentSafety:ApiKey` が未設定の場合は、キーワードベースの**開発用モック**で動作します。

**リクエスト形式 (A2A message/send)**

```json
{
  "jsonrpc": "2.0", "id": "1", "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user", "messageId": "msg-001",
      "parts": [{ "kind": "text", "text": "評価したいテキスト" }]
    }
  }
}
```

**レスポンス例**

```json
{ "category": "Violence", "score": 0, "severity": "None", "flagged": false }
```

---

### Sexual Evaluator

| 項目 | 値 |
|---|---|
| A2A エンドポイント | `POST /agent` |
| AgentCard | `GET /.well-known/agent-card.json` |
| ローカルポート (開発時) | `5201` |
| K8s Service | `sexual-evaluator-svc:80` |

Azure AI Content Safety の `Sexual` カテゴリでテキストを評価します。  
`AzureContentSafety:Endpoint` / `AzureContentSafety:ApiKey` が未設定の場合は、開発用モック（常時 safe）で動作します。

---

### Chatbot

| 項目 | 値 |
|---|---|
| A2A エンドポイント | `POST /agent` |
| AgentCard | `GET /.well-known/agent-card.json` |
| ローカルポート (開発時) | `5202` |
| K8s NodePort | `30200` |

センシティブなキーワードを含むメッセージに対して ViolenceEvaluator / SexualEvaluator を並列呼び出しし、評価結果を踏まえた応答を返します。

**チャットリクエスト例**

```bash
curl -X POST http://localhost:30200/agent \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0", "id": "1", "method": "message/send",
    "params": {
      "message": {
        "kind": "message",
        "role": "user", "messageId": "msg-001",
        "parts": [{ "kind": "text", "text": "こんにちは！" }]
      }
    }
  }'
```

## ローカルデプロイ手順 (Rancher Desktop)

### 前提条件

- Rancher Desktop が起動していること
- `kubectl` が Rancher Desktop クラスタを向いていること
- Docker が起動していること

### 1. イメージビルド

```bash
# SDK イメージを先にキャッシュしてから各エージェントをビルド
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull mcr.microsoft.com/dotnet/aspnet:10.0

docker build -t violence-evaluator:latest ./AgentEvaluation/ViolenceEvaluator
docker build -t sexual-evaluator:latest   ./AgentEvaluation/SexualEvaluator
docker build -t chatbot:latest            ./AgentEvaluation/Chatbot
```

> **Note**: Rancher Desktop は `docker build` したイメージをそのまま K8s から参照できます。`eval $(minikube docker-env)` は不要です。

### 2. デプロイ

```bash
kubectl apply -f AgentEvaluation/infrastructure.yaml

# または付属スクリプトを使用
./scripts/deploy-eval-local.sh
```

### 3. 動作確認

```bash
# Pod が Running になるまで確認
kubectl get pods -n agent-evaluation -w

# AgentCard 取得
curl http://localhost:30200/.well-known/agent-card.json

# チャットリクエスト
curl -X POST http://localhost:30200/agent \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0", "id": "1", "method": "message/send",
    "params": {
      "message": {
        "kind": "message",
        "role": "user", "messageId": "msg-001",
        "parts": [{ "kind": "text", "text": "こんにちは！今日はいい天気ですね。" }]
      }
    }
  }'
```

### 4. 後片付け

```bash
./scripts/deploy-eval-local.sh --delete
# または
kubectl delete namespace agent-evaluation
```

## Azure AI Content Safety の設定 (任意)

未設定の場合は開発用モックで動作しますが、本番評価時は以下のコマンドで Secret を作成します。

```bash
kubectl create secret generic azure-content-safety-secret \
  --from-literal=endpoint="https://<name>.cognitiveservices.azure.com" \
  --from-literal=api-key="<key>" \
  -n agent-evaluation \
  --dry-run=client -o yaml | kubectl apply -f -
```

## 可観測性

Aspire Dashboard を使って全エージェントの OpenTelemetry トレース・メトリクスを確認できます。

| URL | 用途 |
|---|---|
| `http://localhost:30088` | Aspire Dashboard (OTel トレース/メトリクス UI) |
| `http://localhost:30200/metrics` | Chatbot Prometheus メトリクス |

## 注意事項

- `imagePullPolicy: Never` を使用しているため、`docker build` で最新イメージを作成後は `kubectl rollout restart` が必要です。
- A2A SDK v0.3.3-preview のエージェントカードパスは `/.well-known/agent-card.json` です（`/.well-known/agent.json` ではありません）。
- `A2ACardResolver` にはサービスのルート URL（例: `http://violence-evaluator-svc`）を渡す必要があります。
- A2A SDK v0.3.3-preview のリクエストでは `message` オブジェクト自体にも `"kind": "message"` が必須です（`Part` の `"kind": "text"` と合わせて両方必要）。

