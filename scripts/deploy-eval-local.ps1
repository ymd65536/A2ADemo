<#
.SYNOPSIS
マルチエージェントエバリュエーションシステム - ローカルデプロイスクリプト

.DESCRIPTION
対象環境: Minikube (Docker ドライバー推奨) / Rancher Desktop

System Architecture:
  Client → Chatbot (A2A) → ViolenceEvaluator (A2A)
                         → SexualEvaluator   (A2A)

前提条件:
  - minikube または Rancher Desktop が起動していること
  - kubectl が適切なクラスタを向いていること
  - Docker が起動していること
  - 各コンテナイメージがビルド済みであること (下記ビルド手順)

ビルド手順 (Minikube の Docker デーモンを使用):
  minikube docker-env | Invoke-Expression
  docker build -t violence-evaluator:latest ./AgentEvaluation/ViolenceEvaluator
  docker build -t sexual-evaluator:latest   ./AgentEvaluation/SexualEvaluator
  docker build -t chatbot:latest            ./AgentEvaluation/Chatbot
  docker build -t chatbot-viewer:latest     ./AgentEvaluation/ChatbotViewer
  docker build -t evaluation-agent:latest   ./AgentEvaluation/EvaluationAgent

Azure AI Content Safety の設定 (任意: 未設定時はモック評価を使用):
  kubectl create secret generic azure-content-safety-secret `
    --from-literal=endpoint="https://<name>.cognitiveservices.azure.com" `
    --from-literal=api-key="<key>" `
    -n agent-evaluation `
    --dry-run=client -o yaml | kubectl apply -f -

.PARAMETER Delete
指定した場合、全リソースを削除します

.EXAMPLE
.\scripts\deploy-eval-local.ps1              # デプロイ
.\scripts\deploy-eval-local.ps1 -Delete      # 全リソース削除
#>

[CmdletBinding()]
param(
    [switch]$Delete
)

$ErrorActionPreference = "Stop"

$NAMESPACE = "agent-evaluation"
$MANIFEST = "./AgentEvaluation/infrastructure.yaml"

# 削除モード
if ($Delete) {
    Write-Host "==> [削除] namespace/$NAMESPACE を削除します..." -ForegroundColor Yellow
    kubectl delete namespace $NAMESPACE --ignore-not-found
    Write-Host "==> 削除完了" -ForegroundColor Green
    exit 0
}

Write-Host "===================================================" -ForegroundColor Cyan
Write-Host " マルチエージェントエバリュエーションシステム デプロイ" -ForegroundColor Cyan
Write-Host " namespace: $NAMESPACE" -ForegroundColor Cyan
Write-Host "===================================================" -ForegroundColor Cyan

# 1. Namespace とマニフェストの適用
Write-Host ""
Write-Host "==> [1/4] マニフェストを適用します..." -ForegroundColor Yellow
kubectl apply -f $MANIFEST

# 2. Secret の確認
Write-Host ""
Write-Host "==> [2/4] Azure AI Content Safety Secret の確認..." -ForegroundColor Yellow
try {
    $CS_ENDPOINT = kubectl get secret azure-content-safety-secret -n $NAMESPACE `
        -o jsonpath='{.data.endpoint}' 2>$null
} catch {
    $CS_ENDPOINT = ""
}

if ([string]::IsNullOrEmpty($CS_ENDPOINT)) {
    Write-Host "  [情報] azure-content-safety-secret の endpoint が未設定です。" -ForegroundColor Gray
    Write-Host "  評価エージェントは開発用モック (キーワード判定) で動作します。" -ForegroundColor Gray
    Write-Host "  本番評価を行うには以下のコマンドで Secret を設定してください:" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  kubectl create secret generic azure-content-safety-secret ``" -ForegroundColor Gray
    Write-Host "    --from-literal=endpoint=`"https://<name>.cognitiveservices.azure.com`" ``" -ForegroundColor Gray
    Write-Host "    --from-literal=api-key=`"<key>`" ``" -ForegroundColor Gray
    Write-Host "    -n $NAMESPACE ``" -ForegroundColor Gray
    Write-Host "    --dry-run=client -o yaml | kubectl apply -f -" -ForegroundColor Gray
    Write-Host ""
} else {
    Write-Host "  Secret の endpoint が設定されています。Azure AI Content Safety を使用します。" -ForegroundColor Green
}

# 3. Rollout が完了するまで待機
Write-Host ""
Write-Host "==> [3/4] デプロイメントの Ready 待機..." -ForegroundColor Yellow
$DEPLOYMENTS = @(
    "violence-evaluator",
    "sexual-evaluator",
    "chatbot",
    "aspire-dashboard",
    "chatbot-viewer",
    "evaluation-agent"
)

foreach ($dep in $DEPLOYMENTS) {
    Write-Host "  Waiting: $dep ..." -ForegroundColor Gray
    try {
        kubectl rollout status deployment/$dep `
            -n $NAMESPACE `
            --timeout=120s
    } catch {
        Write-Host "  [タイムアウト] $dep の Ready 待機がタイムアウトしました" -ForegroundColor Red
    }
}

# 4. アクセス情報の表示
Write-Host ""
Write-Host "==> [4/4] アクセス情報" -ForegroundColor Yellow
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

# Rancher Desktop / Minikube は localhost で NodePort に直接アクセス可能
$MINIKUBE_IP = "localhost"
Write-Host "  Chatbot (A2A)         : http://${MINIKUBE_IP}:30200/agent" -ForegroundColor White
Write-Host "  EvaluationAgent (A2A) : http://${MINIKUBE_IP}:30204/agent" -ForegroundColor White
Write-Host "  ChatbotViewer (Web UI) : http://${MINIKUBE_IP}:30203" -ForegroundColor White
Write-Host "  Aspire Dashboard (OTel): http://${MINIKUBE_IP}:30088" -ForegroundColor White
Write-Host ""
Write-Host "  AgentCard 確認:" -ForegroundColor White
Write-Host "    curl http://${MINIKUBE_IP}:30200/.well-known/agent-card.json" -ForegroundColor Gray
Write-Host "    curl http://${MINIKUBE_IP}:30204/.well-known/agent-card.json" -ForegroundColor Gray
Write-Host ""
Write-Host "  チャットリクエスト例 (A2A message/send):" -ForegroundColor White
Write-Host @'
    curl -X POST http://localhost:30200/agent `
      -H "Content-Type: application/json" `
      -d '{
        "jsonrpc": "2.0",
        "id": "1",
        "method": "message/send",
        "params": {
          "message": {
            "role": "user",
            "messageId": "msg-001",
            "parts": [{ "text": "こんにちは！今日の天気はどうですか？" }]
          }
        }
      }'
'@ -ForegroundColor Gray
Write-Host ""
Write-Host "  Pod 一覧:" -ForegroundColor White
kubectl get pods -n $NAMESPACE -o wide
Write-Host ""
Write-Host "  Service 一覧:" -ForegroundColor White
kubectl get services -n $NAMESPACE
Write-Host "===================================================" -ForegroundColor Cyan
Write-Host " デプロイ完了" -ForegroundColor Green
Write-Host "===================================================" -ForegroundColor Cyan
