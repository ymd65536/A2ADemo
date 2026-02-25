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

## Step4: Azure Kubernetes Service (AKS) に A2ADispatcher をデプロイする

ローカルの Kubernetes ではなく、Azure Kubernetes Service (AKS) 上に A2ADispatcher 一式をデプロイします。  
イメージは Azure Container Registry (ACR) に保管し、AKS からプルします。

### 前提条件

- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) がインストール済みであること
- `az login` で Azure にサインイン済みであること
- `kubectl` がインストール済みであること

### ファイル構成

| ファイル | 説明 |
|---|---|
| `A2ADispatcher/aks-infrastructure.yaml` | AKS 用マニフェスト（`infrastructure.yaml` の AKS 対応版） |
| `scripts/deploy-to-aks.sh` | リソース作成からデプロイまでを一括実行するスクリプト |

#### ローカル版との主な差異

| 項目 | ローカル版 | AKS 版 |
|---|---|---|
| イメージビルド | `docker build` | `az acr build`（ACR 上でクラウドビルド） |
| `imagePullPolicy` | `Never` | `Always` |
| イメージ参照 | `a2a-dispatcher:latest` | `<ACR>.azurecr.io/a2a-dispatcher:latest` |
| Dispatcher 公開 | `NodePort: 30010` | `LoadBalancer` |
| AgentCardViewer 公開 | `NodePort: 30020` | `LoadBalancer` |

### 1. デプロイスクリプトの実行

変数を指定してスクリプトを実行します（デフォルト値を使う場合は省略可）。

```bash
RESOURCE_GROUP=a2a-demo-rg \
ACR_NAME=<グローバル一意な小文字英数字名> \
AKS_CLUSTER=a2a-demo-aks \
LOCATION=japaneast \
  ./scripts/deploy-to-aks.sh
```

スクリプトは以下の順序で処理します。

1. リソースグループ作成
2. ACR 作成
3. `az acr build` で 4 つのイメージをクラウドビルド＆プッシュ
4. リソースプロバイダー登録（`Microsoft.ContainerService` 等）
5. AKS クラスター作成（`--attach-acr` で ACR と連携）
6. `kubectl` 認証情報の取得
7. マニフェストを適用

> **注意:** `az acr build` はビルドコンテキストを Azure へ送信して ACR 上でビルドするため、ローカルの Docker デーモンは不要です。

### 2. デプロイ完了の確認

> **補足: なぜ `kubectl get pods` で AKS の Pod が見えるのか**
>
> `az aks get-credentials` を実行すると、AKS クラスターの接続情報が `~/.kube/config` に書き込まれ、`kubectl` の向き先（current-context）が自動的に AKS に切り替わります。  
> そのため、以降の `kubectl` コマンドはすべて AKS クラスターに対して実行されます。
>
> ```bash
> # 現在の向き先を確認
> kubectl config current-context   # → a2a-demo-aks
>
> # 登録済みのクラスター一覧と向き先を確認
> kubectl config get-contexts
>
> # ローカル Kubernetes（Docker Desktop など）に戻す場合
> kubectl config use-context docker-desktop
> ```

```bash
kubectl get pods -o wide
kubectl get services
```

以下のような出力が得られれば正常です。

```
NAME                                 READY   STATUS    RESTARTS   AGE
a2a-dispatcher-xxxxxxxxxx-xxxxx      1/1     Running   0          1m
agent-card-viewer-xxxxxxxxxx-xxxxx   1/1     Running   0          1m
echo-agent-xxxxxxxxxx-xxxxx          1/1     Running   0          1m
simple-agent-xxxxxxxxxx-xxxxx        1/1     Running   0          1m
```

### 3. 動作確認

`a2a-dispatcher-svc` の `EXTERNAL-IP` を確認します。

```bash
kubectl get svc a2a-dispatcher-svc
```

外部 IP が割り当てられたらリクエストを送ります。

```bash
DISPATCHER_IP=$(kubectl get svc a2a-dispatcher-svc \
  -o jsonpath='{.status.loadBalancer.ingress[0].ip}')

curl -X POST "http://${DISPATCHER_IP}/agent" \
  -H "Content-Type: application/json" \
  -d '{"requiredCapability": "サンプル .NET エージェント", "message": "こんにちは"}'
```

```json
{
  "agent": "サンプル .NET エージェント",
  "endpoint": "http://10.x.x.x:8080/agent",
  "reply": "[A2A応答] あなたは「こんにちは」と言いましたね。"
}
```

### トラブルシューティング

#### ImagePullBackOff が発生する場合

ACR の RBAC が AKS ノードに伝搬していない可能性があります。

```bash
# ACR と AKS を再アタッチ
az aks update \
  --resource-group <RESOURCE_GROUP> \
  --name <AKS_CLUSTER> \
  --attach-acr <ACR_NAME>
```

#### MissingSubscriptionRegistration エラーが発生する場合

```bash
az provider register --namespace Microsoft.ContainerService --wait
az provider register --namespace Microsoft.Compute --wait
az provider register --namespace Microsoft.Network --wait
```

---

## Step5: OpenTelemetry → Azure Application Insights でトレースを確認する

各エージェントが送信する OpenTelemetry テレメトリを Azure Application Insights で可視化します。

### 構成図

```
各 Pod (Dispatcher / SimpleAgent / EchoAgent / AgentCardViewer)
  ↓ OTLP HTTP (port 4318)
otel-collector-svc  ← k8s/otel-collector.yaml
  ↓ azuremonitor exporter
Application Insights
  ↓
Log Analytics ワークスペース
```

### ファイル構成

| ファイル | 説明 |
|---|---|
| `k8s/otel-collector.yaml` | OTel Collector の Deployment / Service / ConfigMap |
| `scripts/setup-otel.sh` | Application Insights 作成からデプロイまでを一括実行するスクリプト |

### 1. セットアップスクリプトの実行

```bash
RESOURCE_GROUP=a2a-demo-rg \
ACR_NAME=<ACR名> \
AKS_CLUSTER=a2a-demo-aks \
APP_INSIGHTS_NAME=a2a-demo-appinsights \
  ./scripts/setup-otel.sh
```

スクリプトは以下の順序で処理します。

1. リソースプロバイダー登録（`microsoft.insights` 等）
2. Log Analytics ワークスペース作成（プロビジョニング完了まで待機）
3. Application Insights 作成
4. 接続文字列を Kubernetes Secret (`appinsights-secret`) に登録
5. OTel Collector をデプロイ
6. 全エージェントを再起動（OTel エンドポイントを `otel-collector-svc:4318` に切替）

### 2. 動作確認

テストリクエストを送信してトレースを生成します。

```bash
DISPATCHER_IP=$(kubectl get svc a2a-dispatcher-svc \
  -o jsonpath='{.status.loadBalancer.ingress[0].ip}')

curl -X POST "http://${DISPATCHER_IP}/agent" \
  -H "Content-Type: application/json" \
  -d '{"requiredCapability": "サンプル .NET エージェント", "message": "こんにちは"}'
```

### 3. OTel Collector のテレメトリ受信確認

#### OTel Collector のログを確認する

```bash
kubectl logs deployment/otel-collector --tail=30
```

テストリクエスト送信後、以下のようなログが出力されれば OTel Collector がトレースを受信して Application Insights へ転送しています。

```
2026-02-25T20:41:23.702Z  info  Traces  {"otelcol.component.id": "debug", ..., "resource spans": 2, "spans": 8}
```

| ログのキー | 意味 |
|---|---|
| `resource spans` | 受信したリソース（サービス）の数 |
| `spans` | 受信したスパン（操作）の合計数 |

#### OTel Collector の起動状態を確認する

```bash
kubectl get pods -l app=otel-collector
```

`READY 1/1`・`STATUS Running` であれば正常です。  
`CrashLoopBackOff` や `ContainerCreating` のままの場合は以下で原因を確認します。

```bash
kubectl describe pod -l app=otel-collector | grep -A 20 "Events:"
kubectl logs deployment/otel-collector
```

#### `appinsights-secret` の内容を確認する

```bash
kubectl get secret appinsights-secret \
  -o jsonpath='{.data.connection-string}' | base64 --decode
# InstrumentationKey=...;IngestionEndpoint=https://...
```

接続文字列が空の場合、`setup-otel.sh` が正常完了していません。再実行してください。

### 4. Azure Portal での確認方法

| 確認したい内容 | 場所 |
|---|---|
| リクエストのトレース（End-to-End） | Application Insights → 調査 > **トランザクション検索** |
| リアルタイム監視 | Application Insights → 調査 > **ライブメトリクス** |
| サービス間の依存関係マップ | Application Insights → 調査 > **アプリケーション マップ** |
| ログ・クエリ | Application Insights → 監視 > **ログ** |

> **補足:** テスト送信から Application Insights にトレースが反映されるまで **1〜2 分**かかる場合があります。

#### Application Insights でのクエリ例（Kusto Query Language）

トランザクション検索 → **ログ** から以下のクエリを実行できます。

```kusto
// 直近 30 分のリクエスト一覧
requests
| where timestamp > ago(30m)
| project timestamp, name, duration, resultCode
| order by timestamp desc

// サービス間の依存呼び出し
dependencies
| where timestamp > ago(30m)
| project timestamp, name, target, duration, success
| order by timestamp desc
```

### 5. 片付け（AKS リソース削除）

```bash
# K8s リソースをすべて削除
kubectl delete -f A2ADispatcher/aks-infrastructure.yaml
kubectl delete -f k8s/otel-collector.yaml
kubectl delete -f k8s/secret-provider-class.yaml
kubectl delete secret appinsights-secret

# Azure リソース一式を削除（リソースグループごと）
az group delete --name a2a-demo-rg --yes --no-wait
```

---

## Step6: Azure Key Vault でシークレットを安全に管理する

Step5 では `kubectl create secret` で接続文字列を K8s に直接登録しましたが、etcd への保存は base64 エンコードのみで暗号化されていません。  
このステップでは **Azure Key Vault + Secrets Store CSI Driver + Workload Identity** を使い、接続文字列を Key Vault で一元管理します。

### 仕組み

```
Key Vault（接続文字列を保管）
  ↓ Secrets Store CSI Driver（azure-keyvault-secrets-provider アドオン）
SecretProviderClass → K8s Secret に自動同期
  ↓ secretKeyRef（OTel Collector 側のコード変更なし）
OTel Collector Pod（環境変数として注入）
```

Pod が CSI ボリュームをマウントするタイミングで Key Vault からシークレットが取得され、K8s Secret に同期されます。  
Pod を再起動するたびに最新の値が反映されるため、**Key Vault でシークレットを更新するだけでローテーションが完結**します。

### ファイル構成

| ファイル | 説明 |
|---|---|
| `scripts/setup-keyvault.sh` | Key Vault 作成・CSI Driver 有効化・Workload Identity 設定を一括実行 |
| `k8s/secret-provider-class.yaml` | SecretProviderClass のテンプレート（スクリプトが自動適用） |
| `k8s/otel-collector.yaml` | CSI ボリュームマウントと Workload Identity ラベルを追加済み |

### Step5 との差異

| 項目 | Step5（kubectl secret） | Step6（Key Vault） |
|---|---|---|
| 保管場所 | AKS etcd（base64のみ） | Azure Key Vault（暗号化） |
| ローテーション | 手動で `kubectl create secret` を再実行 | Key Vault でシークレット更新 → Pod 再起動で反映 |
| アクセス制御 | K8s RBAC のみ | Azure RBAC + Key Vault アクセスポリシー |
| 監査ログ | なし | Key Vault のアクセスログ（Azure Monitor） |

### 1. セットアップスクリプトの実行

Step5 の `setup-otel.sh` を **先に実行済みであること**（Application Insights が存在すること）が前提です。

```bash
RESOURCE_GROUP=a2a-demo-rg \
AKS_CLUSTER=a2a-demo-aks \
ACR_NAME=<ACR名> \
APP_INSIGHTS_NAME=a2a-demo-appinsights \
KEY_VAULT_NAME=<グローバル一意な3-24文字の名前> \
  ./scripts/setup-keyvault.sh
```

スクリプトは以下の順序で処理します。

1. Key Vault 作成（`--enable-rbac-authorization true`）
2. Application Insights 接続文字列を Key Vault に登録
3. AKS に OIDC Issuer / Workload Identity / CSI Driver アドオンを有効化
4. User-Assigned Managed Identity 作成
5. Managed Identity に **Key Vault Secrets User** ロールを付与
6. K8s ServiceAccount 作成（Managed Identity と紐付け）
7. Federated Credential 作成（K8s SA ↔ Managed Identity を連携）
8. SecretProviderClass を適用
9. OTel Collector を再デプロイ

### 2. 同期確認

Pod 起動後、Key Vault から K8s Secret に同期されていることを確認します。

```bash
# K8s Secret が作成されていることを確認
kubectl get secret appinsights-secret

# 中身を確認（Key Vault の値と一致するはず）
kubectl get secret appinsights-secret \
  -o jsonpath='{.data.connection-string}' | base64 --decode
```

### 3. シークレットのローテーション

Key Vault でシークレットを更新した場合、Pod を再起動するだけで反映されます。

```bash
# Key Vault でシークレットを更新
az keyvault secret set \
  --vault-name <KEY_VAULT_NAME> \
  --name appinsights-connection-string \
  --value "<新しい接続文字列>"

# Pod を再起動して最新値を反映
kubectl rollout restart deployment/otel-collector
```

### 4. 片付け

```bash
kubectl delete -f k8s/otel-collector.yaml
kubectl delete -f k8s/secret-provider-class.yaml
kubectl delete serviceaccount otel-collector-sa

# Azure リソースごと削除する場合
az group delete --name a2a-demo-rg --yes --no-wait
```

## memo

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
