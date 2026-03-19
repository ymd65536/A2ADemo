#!/usr/bin/env bash
# ===========================================================
# マルチエージェントエバリュエーションシステム - ローカルデプロイスクリプト
# 対象環境: Minikube (Docker ドライバー推奨)
#
# System Architecture:
#   Client → Chatbot (A2A) → ViolenceEvaluator (A2A)
#                          → SexualEvaluator   (A2A)
#
# 前提条件:
#   - minikube が起動していること       (minikube start)
#   - kubectl が minikube を向いていること (kubectl config use-context minikube)
#   - Docker が起動していること
#   - 各コンテナイメージがビルド済みであること (下記ビルド手順)
#
# ビルド手順 (Minikube の Docker デーモンを使用):
#   eval $(minikube docker-env)
#   docker build -t violence-evaluator:latest ./AgentEvaluation/ViolenceEvaluator
#   docker build -t sexual-evaluator:latest   ./AgentEvaluation/SexualEvaluator
#   docker build -t chatbot:latest            ./AgentEvaluation/Chatbot
#
# Azure AI Content Safety の設定 (任意: 未設定時はモック評価を使用):
#   kubectl create secret generic azure-content-safety-secret \
#     --from-literal=endpoint="https://<name>.cognitiveservices.azure.com" \
#     --from-literal=api-key="<key>" \
#     -n agent-evaluation \
#     --dry-run=client -o yaml | kubectl apply -f -
#
# 使い方:
#   ./scripts/deploy-eval-local.sh              # デプロイ
#   ./scripts/deploy-eval-local.sh --delete     # 全リソース削除
# ===========================================================
set -euo pipefail

NAMESPACE="agent-evaluation"
MANIFEST="./AgentEvaluation/infrastructure.yaml"

# ヘルプ
if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  sed -n '1,35p' "$0" | grep '^#' | sed 's/^# \?//'
  exit 0
fi

# 削除モード
if [[ "${1:-}" == "--delete" ]]; then
  echo "==> [削除] namespace/$NAMESPACE を削除します..."
  kubectl delete namespace "$NAMESPACE" --ignore-not-found
  echo "==> 削除完了"
  exit 0
fi

echo "==================================================="
echo " マルチエージェントエバリュエーションシステム デプロイ"
echo " namespace: $NAMESPACE"
echo "==================================================="

# 1. Namespace とマニフェストの適用
echo ""
echo "==> [1/4] マニフェストを適用します..."
kubectl apply -f "$MANIFEST"

# 2. Secret の確認
echo ""
echo "==> [2/4] Azure AI Content Safety Secret の確認..."
CS_ENDPOINT=$(kubectl get secret azure-content-safety-secret -n "$NAMESPACE" \
  -o jsonpath='{.data.endpoint}' 2>/dev/null || echo "")
if [[ -z "$CS_ENDPOINT" ]]; then
  echo "  [情報] azure-content-safety-secret の endpoint が未設定です。"
  echo "  評価エージェントは開発用モック (キーワード判定) で動作します。"
  echo "  本番評価を行うには以下のコマンドで Secret を設定してください:"
  echo ""
  echo "  kubectl create secret generic azure-content-safety-secret \\"
  echo "    --from-literal=endpoint=\"https://<name>.cognitiveservices.azure.com\" \\"
  echo "    --from-literal=api-key=\"<key>\" \\"
  echo "    -n $NAMESPACE \\"
  echo "    --dry-run=client -o yaml | kubectl apply -f -"
  echo ""
else
  echo "  Secret の endpoint が設定されています。Azure AI Content Safety を使用します。"
fi

# 3. Rollout が完了するまで待機
echo ""
echo "==> [3/4] デプロイメントの Ready 待機..."
DEPLOYMENTS=(
  "violence-evaluator"
  "sexual-evaluator"
  "chatbot"
  "aspire-dashboard"
)
for dep in "${DEPLOYMENTS[@]}"; do
  echo "  Waiting: $dep ..."
  kubectl rollout status deployment/"$dep" \
    -n "$NAMESPACE" \
    --timeout=120s || echo "  [タイムアウト] $dep の Ready 待機がタイムアウトしました"
done

# 4. アクセス情報の表示
echo ""
echo "==> [4/4] アクセス情報"
echo "---------------------------------------------------"
# Rancher Desktop は localhost で NodePort に直接アクセス可能
MINIKUBE_IP="localhost"
echo "  Chatbot (A2A)         : http://${MINIKUBE_IP}:30200/agent"
echo "  Aspire Dashboard (OTel): http://${MINIKUBE_IP}:30088"
echo ""
echo "  AgentCard 確認:"
echo "    curl http://${MINIKUBE_IP}:30200/.well-known/agent.json"
echo ""
echo "  チャットリクエスト例 (A2A message/send):"
cat <<'EOF'
    curl -X POST http://<MINIKUBE_IP>:30200/agent \
      -H "Content-Type: application/json" \
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
EOF
echo ""
echo "  Pod 一覧:"
kubectl get pods -n "$NAMESPACE" -o wide
echo ""
echo "  Service 一覧:"
kubectl get services -n "$NAMESPACE"
echo "==================================================="
echo " デプロイ完了"
echo "==================================================="
