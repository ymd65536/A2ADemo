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
3. **Chatbot**: チャットボットエージェント。ユーザーとの対話を通じて、様々なタスクやシナリオに対して評価を行います。応答内容はいったん各Evaluatorを呼び出してから返す形であり、Evaluatorを呼び出すかどうかはユーザーからの質問内容によって判断されます。
4. **EvaluationAgent**: `[User]/[Chatbot]` 形式の Q&A ペアを受け取り、ViolenceEvaluator / SexualEvaluator を A2A 呼び出しして評価し、評価結果をまとめて返すエージェント。

4つのエージェントとは別に、Blazor Server で実装された **ChatbotViewer** が Kubernetes 上で動作し、Chatbot の AgentCard 確認とメッセージ送信を行える Web UI として機能します。

### A2A 接続フロー

```
Browser
  └─ HTTP → ChatbotViewer (:30203)  ← Blazor Server Web UI
               └─ HTTP POST /agent (A2A message/send)
                    └─ Chatbot (:30200)
                         └─ A2A → EvaluationAgent (evaluation-agent-svc:80)  ← [User]/[Chatbot] Q&A ペアを渡す
                              ├─ A2A → ViolenceEvaluator (violence-evaluator-svc:80)  ← 常時呼び出し
                              └─ A2A → SexualEvaluator   (sexual-evaluator-svc:80)    ← 性的キーワード検出時

# EvaluationAgent は単体でも呼び出し可能
Client
  └─ HTTP POST /agent ("[User]\n...\n[Chatbot]\n..." 形式)
       └─ EvaluationAgent (:30204)
            ├─ A2A → ViolenceEvaluator (violence-evaluator-svc:80)
            └─ A2A → SexualEvaluator   (sexual-evaluator-svc:80)  ← 性的キーワード検出時
```

Chatbot はユーザーメッセージを受信し、センシティブなキーワードを含む場合に EvaluationAgent を A2A で呼び出します。Q&A ペアを `[User]/[Chatbot]` 形式で渡し、EvaluationAgent の評価結果をそのまま返します。

EvaluationAgent は `[User]/[Chatbot]` 形式の Q&A ペアを受け取り、ViolenceEvaluator を常時呼び出し、性的キーワードが含まれる場合は SexualEvaluator も呼び出します。評価結果を `[Evaluation]` セクションとして Q&A ペアに付加して返します。

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
├── Chatbot/                     # エージェント 3 (オーケストレーター)
│   ├── Chatbot.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── dockerfile
│   └── Properties/launchSettings.json
├── ChatbotViewer/               # Web UI (Blazor Server)
│   ├── ChatbotViewer.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   ├── dockerfile
│   ├── Components/
│   │   ├── App.razor
│   │   ├── Layout/              # MainLayout, NavMenu
│   │   └── Pages/               # Home (AgentCard表示), SendRequest (メッセージ送信)
│   └── wwwroot/
└── EvaluationAgent/             # エージェント 4 (Q&A ペア評価オーケストレーター)
    ├── EvaluationAgent.csproj
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
| K8s NodePort | `30201` |

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

**直接呼び出し例**

```bash
# K8s NodePort 経由 (kubectl port-forward 不要)
curl -s -X POST http://localhost:30201/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"kill the enemy"}]}}}'
```

---

### Sexual Evaluator

| 項目 | 値 |
|---|---|
| A2A エンドポイント | `POST /agent` |
| AgentCard | `GET /.well-known/agent-card.json` |
| ローカルポート (開発時) | `5201` |
| K8s Service | `sexual-evaluator-svc:80` |
| K8s NodePort | `30202` |

Azure AI Content Safety の `Sexual` カテゴリでテキストを評価します。  
`AzureContentSafety:Endpoint` / `AzureContentSafety:ApiKey` が未設定の場合は、開発用モック（常時 safe）で動作します。

**直接呼び出し例**

```bash
# K8s NodePort 経由 (kubectl port-forward 不要)
curl -s -X POST http://localhost:30202/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"評価したいテキスト"}]}}}'
```

---

### Chatbot

| 項目 | 値 |
|---|---|
| A2A エンドポイント | `POST /agent` |
| AgentCard | `GET /.well-known/agent-card.json` |
| ローカルポート (開発時) | `5202` |
| K8s NodePort | `30200` |
| EvaluationAgent 接続先 (K8s内) | `http://evaluation-agent-svc` |
| AI エンジン | Azure OpenAI (Microsoft Agent Framework) / モックフォールバック |

**Microsoft Agent Framework** (`Microsoft.Agents.AI.OpenAI`) を使用して Azure OpenAI でユーザーメッセージに応答します。  
Azure OpenAI が未設定の場合はモック応答にフォールバックして動作します (開発・テスト用)。  
センシティブなキーワードを含むメッセージに対して EvaluationAgent を A2A で呼び出しし、評価結果をそのまま返します。

**Chatbot の内部フロー**

```
POST /agent (ユーザーメッセージ受信)
  │
  ├─ Azure OpenAI が設定済み → AIAgent.RunAsync() で LLM 応答を生成
  │   └─ 未設定 → モック応答 (開発用フォールバック)
  │
  ├─ キーワード判定 (NeedsEvaluation)
  │    ├─ 該当なし → "[User]\n...\n[Chatbot]\n..." ペアをそのまま返す
  │    └─ 該当あり → Q&A ペアを構築し EvaluationAgent を A2A で呼び出し
  │
  └─ A2A POST /agent → EvaluationAgent
            └─ 評価結果 ([Evaluation] セクション付き) をそのまま返す
```

センシティブと判断されるキーワード例: `kill`, `attack`, `bomb`, `sex`, `殺`, `暴力`, `性的` など

**Azure OpenAI の環境変数**

| 変数名 | 説明 |
|---|---|
| `AzureOpenAI__Endpoint` | Azure OpenAI エンドポイント (例: `https://<name>.openai.azure.com`) |
| `AzureOpenAI__DeploymentName` | デプロイ名 (例: `gpt-4o`) |
| `AzureOpenAI__ApiKey` | API キー (省略時は DefaultAzureCredential を使用) |

> Azure OpenAI の設定手順は [Azure OpenAI の設定](#azure-openai-の設定-任意) を参照してください。

---

### ChatbotViewer

| 項目 | 値 |
|---|---|
| URL | `http://localhost:30203` |
| ローカルポート (開発時) | `5100` |
| K8s NodePort | `30203` |
| K8s Service | `chatbot-viewer-svc:80` |
| 接続先 (K8s内) | `http://chatbot-svc` |

Chatbot の AgentCard 情報を表示し、メッセージを A2A 形式で送信できる Blazor Server Web UI です。

**機能**
- **AgentCard ページ** (`/`): Chatbot の名前・説明・エンドポイント・機能一覧をカード表示
- **メッセージ送信ページ** (`/send-request`): テキストを入力して Chatbot に A2A リクエストを送信し、レスポンスを表示

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
        "parts": [{ "kind": "text", "text": "attack!!" }]
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
docker build -t chatbot-viewer:latest     ./AgentEvaluation/ChatbotViewer
docker build -t evaluation-agent:latest   ./AgentEvaluation/EvaluationAgent
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

# AgentCard 取得 (Bash)
curl http://localhost:30200/.well-known/agent-card.json

# AgentCard 取得 (PowerShell)
Invoke-RestMethod -Uri "http://localhost:30200/.well-known/agent-card.json"
```

> **Web UI でも確認できます**: ブラウザで `http://localhost:30203` を開くと ChatbotViewer が起動しており、AgentCard の確認とメッセージ送信を GUI で行えます。

---

### EvaluationAgent

| 項目 | 値 |
|---|---|
| A2A エンドポイント | `POST /agent` |
| AgentCard | `GET /.well-known/agent-card.json` |
| ローカルポート (開発時) | `5203` |
| K8s NodePort | `30204` |
| K8s Service | `evaluation-agent-svc:80` |
| ViolenceEvaluator 接続先 | `http://violence-evaluator-svc` |
| SexualEvaluator 接続先 | `http://sexual-evaluator-svc` |

`[User]\n{質問}\n[Chatbot]\n{回答}` 形式のテキストを受け取り、Violence / Sexual 評価を行います。
- **モック動作**: ViolenceEvaluator は常時呼び出し、SexualEvaluator は性的キーワード検出時のみ呼び出し
- `AzureContentSafety` 未設定時は各 Evaluator のモック動作に依存

**リクエスト形式**

```json
{
  "jsonrpc": "2.0", "id": "1", "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user", "messageId": "msg-001",
      "parts": [{ "kind": "text", "text": "[User]\nattack!!\n[Chatbot]\n申し訳ありませんが、そのリクエストにはお応えできません。" }]
    }
  }
}
```

**レスポンス例**

```
[User]
attack!!
[Chatbot]
申し訳ありませんが、そのリクエストにはお応えできません。
[Evaluation]
Violence: score=4 (Medium) ⚠️ flagged

総合判定: ⚠️ 問題のあるコンテンツを検出
```

**直接呼び出し例**

```bash
curl -s -X POST http://localhost:30204/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"[User]\nattack!!\n[Chatbot]\n申し訳ありません。"}]}}}'
```

---

#### 3-1. 通常メッセージ (評価スキップ)

センシティブキーワードを含まないメッセージ。評価エージェントは呼び出されず、そのまま応答します。

**Bash:**
```bash
curl -s -X POST http://localhost:30200/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"こんにちは！今日はいい天気ですね。"}]}}}'
```

**PowerShell:**
```powershell
$body = @'
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user",
      "messageId": "msg-001",
      "parts": [{"kind": "text", "text": "こんにちは！今日はいい天気ですね。"}]
    }
  }
}
'@
$response = Invoke-RestMethod -Uri "http://localhost:30200/agent" -Method Post -Body $body -ContentType "application/json"
$response.result.parts[0].text
```

期待レスポンス: `[Chatbot] こんにちは！... について承りました。`

#### 3-2. 暴力的コンテンツの評価 (EvaluationAgent 経由)

`kill`, `attack`, `bomb`, `殺`, `暴力` などのキーワードを含むメッセージ。Chatbot が Q&A ペアを EvaluationAgent に渡し、EvaluationAgent が ViolenceEvaluator を呼び出します。

**Bash:**
```bash
curl -s -X POST http://localhost:30200/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-002","parts":[{"kind":"text","text":"attack!!"}]}}}'
```

**PowerShell:**
```powershell
$body = @'
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user",
      "messageId": "msg-002",
      "parts": [{"kind": "text", "text": "attack!!"}]
    }
  }
}
'@
$response = Invoke-RestMethod -Uri "http://localhost:30200/agent" -Method Post -Body $body -ContentType "application/json"
$response.result.parts[0].text
```

期待レスポンス: EvaluationAgent から返ってくる `[Evaluation]` セクション付きの評価結果

#### 3-3. 性的コンテンツの評価 (EvaluationAgent 経由)

`sex`, `nude`, `sexual`, `性的`, `裸`, `ポルノ` などのキーワードを含むメッセージ。Chatbot が Q&A ペアを EvaluationAgent に渡し、EvaluationAgent が ViolenceEvaluator + SexualEvaluator 両方を呼び出します。

**Bash:**
```bash
curl -s -X POST http://localhost:30200/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-003","parts":[{"kind":"text","text":"sexual content"}]}}}'
```

**PowerShell:**
```powershell
$body = @'
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user",
      "messageId": "msg-003",
      "parts": [{"kind": "text", "text": "sexual content"}]
    }
  }
}
'@
$response = Invoke-RestMethod -Uri "http://localhost:30200/agent" -Method Post -Body $body -ContentType "application/json"
$response.result.parts[0].text
```

期待レスポンス: EvaluationAgent から返ってくる `[Evaluation]` セクション付きの評価結果 (Violence + Sexual 両方)

#### 3-4. 各 Evaluator への直接呼び出し

Chatbot を経由せず、個別エージェントを直接呼び出す場合:

**Bash:**
```bash
# ViolenceEvaluator を直接呼び出し
curl -s -X POST http://localhost:30201/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"kill the enemy"}]}}}'

# SexualEvaluator を直接呼び出し
curl -s -X POST http://localhost:30202/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"Hello, how are you?"}]}}}'
```

**PowerShell:**
```powershell
# ViolenceEvaluator を直接呼び出し
$body = @'
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user",
      "messageId": "msg-001",
      "parts": [{"kind": "text", "text": "kill the enemy"}]
    }
  }
}
'@
$response = Invoke-RestMethod -Uri "http://localhost:30201/agent" -Method Post -Body $body -ContentType "application/json"
$response.result.parts[0].text
# 期待結果: {"category":"Violence","score":4,"severity":"Medium","flagged":true}

# SexualEvaluator を直接呼び出し
$body = @'
{
  "jsonrpc": "2.0",
  "id": "1",
  "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user",
      "messageId": "msg-002",
      "parts": [{"kind": "text", "text": "Hello, how are you?"}]
    }
  }
}
'@
$response = Invoke-RestMethod -Uri "http://localhost:30202/agent" -Method Post -Body $body -ContentType "application/json"
$response.result.parts[0].text
# 期待結果: {"category":"Sexual","score":0,"severity":"None","flagged":false}
```

### 4. 後片付け

```bash
./scripts/deploy-eval-local.sh --delete
# または
kubectl delete namespace agent-evaluation
```

## Azure AI Content Safety の設定 (任意)

未設定の場合は開発用モックで動作しますが、本番評価時は Azure AI Content Safety を使用します。  
`ASPNETCORE_ENVIRONMENT=Production` の場合、設定がないとアプリ起動時に例外が発生します。

### 前提条件

- Azure サブスクリプションがあること
- Azure CLI がインストールされていること (`az --version` で確認)

### 1. Azure AI Content Safety リソースの作成

**Azure Portal を使う場合:**

1. [Azure Portal](https://portal.azure.com) にサインイン
2. 「リソースの作成」→「AI + Machine Learning」→「Content Safety」を選択
3. 以下の項目を入力してリソースを作成:
   - **サブスクリプション**: 使用するサブスクリプション
   - **リソースグループ**: 任意のリソースグループ
   - **リージョン**: `East US` など (Content Safety がサポートされているリージョン)
   - **名前**: 任意の名前 (例: `my-content-safety`)
   - **価格レベル**: `Free F0` (月 5,000 テキスト解析まで無料) または `Standard S0`

**Azure CLI を使う場合:**

```bash
# リソースグループ作成 (既存のものを使う場合は不要)
az group create --name my-rg --location eastus

# Content Safety リソース作成
az cognitiveservices account create \
  --name my-content-safety \
  --resource-group my-rg \
  --kind ContentSafety \
  --sku F0 \
  --location eastus \
  --yes
```

### 2. エンドポイントと API キーの取得

**Azure Portal を使う場合:**

1. 作成したリソースを開く
2. 左メニュー「リソース管理」→「キーとエンドポイント」を選択
3. **エンドポイント** と **キー 1** をコピー

**Azure CLI を使う場合:**

```bash
# エンドポイント取得
az cognitiveservices account show \
  --name my-content-safety \
  --resource-group my-rg \
  --query "properties.endpoint" -o tsv

# API キー取得
az cognitiveservices account keys list \
  --name my-content-safety \
  --resource-group my-rg \
  --query "key1" -o tsv
```

### 3. Kubernetes Secret の作成

取得したエンドポイントと API キーを Kubernetes Secret に設定します。

```bash
kubectl create secret generic azure-content-safety-secret \
  --from-literal=endpoint="https://<name>.cognitiveservices.azure.com" \
  --from-literal=api-key="<key>" \
  -n agent-evaluation \
  --dry-run=client -o yaml | kubectl apply -f -
```

> `<name>` と `<key>` をステップ 2 で取得した値に置き換えてください。

**PowerShell の場合:**

```powershell
$endpoint = "https://<name>.cognitiveservices.azure.com"
$apiKey   = "<key>"

kubectl create secret generic azure-content-safety-secret `
  --from-literal=endpoint="$endpoint" `
  --from-literal=api-key="$apiKey" `
  -n agent-evaluation `
  --dry-run=client -o yaml | kubectl apply -f -
```

### 4. Pod の再起動

Secret を反映させるために ViolenceEvaluator と SexualEvaluator を再起動します。

```bash
kubectl rollout restart deployment/violence-evaluator deployment/sexual-evaluator -n agent-evaluation
kubectl rollout status deployment/violence-evaluator deployment/sexual-evaluator -n agent-evaluation
```

### 5. 動作確認

再起動後、Azure AI Content Safety が有効になっていることを確認します。

```bash
# ViolenceEvaluator のログで "ContentSafetyClient" が初期化されているか確認
kubectl logs -n agent-evaluation deployment/violence-evaluator --tail=20

# 実際にリクエストを送信して Azure Content Safety による評価結果を確認
curl -s -X POST http://localhost:30201/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"kill the enemy"}]}}}'
```

PowerShell の場合:

```powershell
$body = '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"kill the enemy"}]}}}'
$response = Invoke-RestMethod -Uri "http://localhost:30201/agent" -Method Post -Body $body -ContentType "application/json"
$response.result.parts[0].text
```

Secret を削除してモックに戻す場合:

```bash
kubectl delete secret azure-content-safety-secret -n agent-evaluation
kubectl rollout restart deployment/violence-evaluator deployment/sexual-evaluator -n agent-evaluation
```

> **注意**: `ASPNETCORE_ENVIRONMENT=Production` の場合は Secret がないと起動に失敗します。`infrastructure.yaml` のデフォルト環境変数は `Development` のため、ローカル Rancher Desktop 環境では Secret なしでも起動できます。

---

## Azure OpenAI の設定 (任意)

未設定の場合は開発用モックで動作します。Azure OpenAI を設定することで、Chatbot が実際の LLM で応答するようになります。

### 前提条件

- Azure サブスクリプションがあること
- Azure AI Foundry プロジェクトがあること、または Azure OpenAI リソースがあること

### 1. Azure OpenAI リソースの作成

**Azure Portal を使う場合:**

1. [Azure AI Foundry](https://ai.azure.com) または [Azure Portal](https://portal.azure.com) にサインイン
2. Azure AI Foundry の場合: プロジェクトを作成し、モデル (`gpt-4o` など) をデプロイ
3. Azure Portal の場合: 「リソースの作成」→「Azure OpenAI」を選択してリソースを作成

**Azure CLI を使う場合:**

```bash
# Azure OpenAI リソース作成
az cognitiveservices account create \
  --name my-openai \
  --resource-group my-rg \
  --kind OpenAI \
  --sku S0 \
  --location eastus \
  --yes

# モデルをデプロイ
az cognitiveservices account deployment create \
  --name my-openai \
  --resource-group my-rg \
  --deployment-name gpt-4o \
  --model-name gpt-4o \
  --model-version "2024-08-06" \
  --model-format OpenAI \
  --sku-capacity 10 \
  --sku-name GlobalStandard
```

### 2. エンドポイントとデプロイ名の取得

**Azure Portal を使う場合:**

1. 作成したリソース (または AI Foundry プロジェクト) を開く
2. **エンドポイント** と **デプロイ名** をコピー
3. API キーが必要な場合: 「リソース管理」→「キーとエンドポイント」から **キー 1** をコピー

**Azure CLI を使う場合:**

```bash
# エンドポイント取得
az cognitiveservices account show \
  --name my-openai \
  --resource-group my-rg \
  --query "properties.endpoint" -o tsv

# API キー取得 (Workload Identity を使う場合は不要)
az cognitiveservices account keys list \
  --name my-openai \
  --resource-group my-rg \
  --query "key1" -o tsv
```

### 3. Kubernetes Secret の作成

```bash
kubectl create secret generic azure-openai-secret \
  --from-literal=endpoint="https://<name>.openai.azure.com" \
  --from-literal=deployment-name="gpt-4o" \
  --from-literal=api-key="<key>" \
  -n agent-evaluation \
  --dry-run=client -o yaml | kubectl apply -f -
```

> AKS Workload Identity を使う場合は `api-key` を省略できます。その場合 `DefaultAzureCredential` が使用されます。

**PowerShell の場合:**

```powershell
$endpoint       = "https://<name>.openai.azure.com"
$deploymentName = "gpt-4o"
$apiKey         = "<key>"

kubectl create secret generic azure-openai-secret `
  --from-literal=endpoint="$endpoint" `
  --from-literal=deployment-name="$deploymentName" `
  --from-literal=api-key="$apiKey" `
  -n agent-evaluation `
  --dry-run=client -o yaml | kubectl apply -f -
```

### 4. Pod の再起動

```bash
kubectl rollout restart deployment/chatbot -n agent-evaluation
kubectl rollout status deployment/chatbot -n agent-evaluation
```

### 5. 動作確認

```bash
# Chatbot のログで "AIAgent 応答完了" が出力されているか確認
kubectl logs -n agent-evaluation deployment/chatbot --tail=20

# 通常メッセージ送信テスト
curl -s -X POST http://localhost:30200/agent \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":"1","method":"message/send","params":{"message":{"kind":"message","role":"user","messageId":"msg-001","parts":[{"kind":"text","text":"東京の天気を教えてください"}]}}}'
```

Secret を削除してモックに戻す場合:

```bash
kubectl delete secret azure-openai-secret -n agent-evaluation
kubectl rollout restart deployment/chatbot -n agent-evaluation
```

## 可観測性

Aspire Dashboard を使って全エージェントの OpenTelemetry トレース・メトリクスを確認できます。

| URL | 用途 |
|---|---|
| `http://localhost:30200` | Chatbot (A2A エンドポイント) |
| `http://localhost:30201` | ViolenceEvaluator (A2A エンドポイント) |
| `http://localhost:30202` | SexualEvaluator (A2A エンドポイント) |
| `http://localhost:30203` | **ChatbotViewer** (Blazor Server Web UI) |
| `http://localhost:30204` | **EvaluationAgent** (A2A エンドポイント) |
| `http://localhost:30088` | Aspire Dashboard (OTel トレース/メトリクス UI) |
| `http://localhost:30200/metrics` | Chatbot Prometheus メトリクス |

## 注意事項

- `imagePullPolicy: Never` を使用しているため、`docker build` で最新イメージを作成後は `kubectl rollout restart` が必要です。
- A2A SDK v0.3.3-preview のエージェントカードパスは `/.well-known/agent-card.json` です（`/.well-known/agent.json` ではありません）。
- `A2ACardResolver` にはサービスのルート URL（例: `http://violence-evaluator-svc`）を渡す必要があります。
- A2A SDK v0.3.3-preview のリクエストでは `message` オブジェクト自体にも `"kind": "message"` が必須です（`Part` の `"kind": "text"` と合わせて両方必要）。

