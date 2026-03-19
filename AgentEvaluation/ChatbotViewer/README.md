# ChatbotViewer

Chatbot エージェントの AgentCard を表示し、メッセージを送信できる Blazor Server アプリケーション

## 概要

このアプリケーションは、AgentEvaluation システムの Chatbot エージェントと A2A プロトコルで通信し、以下の機能を提供します：

- **AgentCard 表示**: Chatbot の AgentCard (名前、説明、エンドポイント、機能) をカード形式で表示
- **メッセージ送信**: Chatbot にメッセージを送信し、Violence/Sexual 評価を含むレスポンスを受信
- **送信履歴**: メッセージの送受信履歴を表示（フラグ付きコンテンツを視覚的に識別）

## 機能

### 1. Home 画面 (`/`)

Chatbot の AgentCard を表示します：
- エージェント名と説明
- エンドポイント URL
- ストリーミング対応状況
- 機能とエクステンション一覧
- Raw JSON 表示

### 2. メッセージ送信画面 (`/send-request`)

Chatbot にメッセージを送信してレスポンスを表示：
- テキストエリアでメッセージを入力
- A2A JSON-RPC プロトコルで送信
- レスポンスを整形表示（メッセージ ID、返答テキスト、Raw JSON）
- 送信履歴を最大10件保持
- フラグ付きコンテンツ（暴力的/性的）を警告バッジで表示

## セットアップ

### 前提条件

- .NET 10.0 SDK
- Kubernetes クラスタ (Rancher Desktop など)
- Chatbot が agent-evaluation 名前空間で稼働中

### デプロイ

#### ローカル開発環境

```powershell
# Chatbot が稼働していることを確認
kubectl get pods -n agent-evaluation | Select-String "chatbot"

# プロジェクトディレクトリに移動
cd AgentEvaluation/ChatbotViewer

# アプリケーションを起動
dotnet run
```

アプリケーションは `http://localhost:5100` で起動します。

#### 設定

`appsettings.Development.json` で Chatbot の URL を設定できます：

```json
{
  "ChatbotUrl": "http://localhost:30200"
}
```

本番環境では `appsettings.json` の `ChatbotUrl` を調整してください（デフォルト: `http://chatbot-svc`）

## 使い方

### AgentCard の確認

1. ブラウザで `http://localhost:5100` にアクセス
2. 「AgentCard を読み込む」ボタンをクリック
3. Chatbot の AgentCard が表示されます
4. 「メッセージを送信する」ボタンでメッセージ送信画面へ遷移

### メッセージの送信

1. メニューから「メッセージ送信」をクリック、または Home 画面から遷移
2. テキストエリアにメッセージを入力：
   - 通常メッセージ: `"こんにちは！"`
   - 暴力的コンテンツテスト: `"attack!!"`
   - 性的コンテンツテスト: `"sexual content"`
3. 「送信」ボタンをクリック
4. レスポンスが表示されます：
   - 評価結果（Violence / Sexual スコア）
   - Chatbot の返答
   - Raw JSON

### 送信履歴の確認

メッセージ送信後、画面下部に履歴が表示されます：
- 🟢 正常: フラグなし
- 🟡 フラグ付き: 暴力的または性的コンテンツを検出
- 🔴 エラー: 送信失敗

## アーキテクチャ

```
ChatbotViewer (Blazor Server)
    ↓ HTTP Client
Chatbot (http://localhost:30200 / chatbot-svc)
    ↓ A2A Protocol
ViolenceEvaluator / SexualEvaluator
```

### A2A JSON-RPC リクエスト例

```json
{
  "jsonrpc": "2.0",
  "id": "unique-id",
  "method": "message/send",
  "params": {
    "message": {
      "kind": "message",
      "role": "user",
      "messageId": "msg-id",
      "parts": [
        { "kind": "text", "text": "こんにちは" }
      ]
    }
  }
}
```

### A2A レスポンス例

```json
{
  "jsonrpc": "2.0",
  "id": "unique-id",
  "result": {
    "kind": "message",
    "role": "agent",
    "messageId": "response-id",
    "parts": [
      {
        "kind": "text",
        "text": "[Chatbot] こんにちは について承りました。..."
      }
    ]
  }
}
```

## 技術スタック

- **フレームワーク**: .NET 10.0 / Blazor Server
- **UI**: Bootstrap 5.3
- **通信**: HttpClient (A2A JSON-RPC)
- **監視**: OpenTelemetry (Tracing, Metrics)

## トラブルシューティング

### Chatbot に接続できない

```powershell
# Chatbot のサービスを確認
kubectl get svc -n agent-evaluation | Select-String "chatbot"

# Chatbot の Pod ログを確認
kubectl logs -n agent-evaluation deployment/chatbot
```

### AgentCard が取得できない

- `http://localhost:30200/.well-known/agent-card.json` にブラウザでアクセスして確認
- `appsettings.Development.json` の `ChatbotUrl` が正しいか確認

## 参考

- [AgentEvaluation README](../README.md)
- [A2A SDK Documentation](https://github.com/microsoft/A2A)
- [AgentCardViewer](../../A2ADispatcher/AgentCardViewer) - 類似プロジェクト（Dispatcher 用）
