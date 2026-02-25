#!/usr/bin/env bash
# =============================================================
# setup-otel.sh
# Application Insights を作成し、OTel Collector を AKS へデプロイする
# =============================================================
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-a2a-demo-rg}"
LOCATION="${LOCATION:-japaneast}"
APP_INSIGHTS_NAME="${APP_INSIGHTS_NAME:-a2a-demo-appinsights}"
AKS_CLUSTER="${AKS_CLUSTER:-a2a-demo-aks}"
ACR_NAME="${ACR_NAME:-myuniquacr}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

log() { echo "[$(date '+%H:%M:%S')] $*"; }

# ---------------------------------------------------------------
# 1. Azure ログイン確認
# ---------------------------------------------------------------
log "=== Azure ログイン確認 ==="
if ! az account show &>/dev/null; then
  az login
fi

# ---------------------------------------------------------------
# 2. Application Insights 用リソースプロバイダー登録
# ---------------------------------------------------------------
log "=== リソースプロバイダー登録 ==="
az provider register --namespace microsoft.insights --wait
az provider register --namespace microsoft.operationalinsights --wait

# ---------------------------------------------------------------
# 3. Log Analytics ワークスペース作成（Application Insights の背後）
# ---------------------------------------------------------------
LOG_ANALYTICS_NAME="${APP_INSIGHTS_NAME}-laws"
log "=== Log Analytics ワークスペース作成（既存の場合はスキップ）: ${LOG_ANALYTICS_NAME} ==="
az monitor log-analytics workspace create \
  --resource-group "${RESOURCE_GROUP}" \
  --workspace-name "${LOG_ANALYTICS_NAME}" \
  --location "${LOCATION}" \
  --output none 2>/dev/null || true

# プロビジョニング完了まで待機
log "=== Log Analytics ワークスペースのプロビジョニング完了を待機 ==="
for i in $(seq 1 20); do
  STATE=$(az monitor log-analytics workspace show \
    --resource-group "${RESOURCE_GROUP}" \
    --workspace-name "${LOG_ANALYTICS_NAME}" \
    --query provisioningState --output tsv 2>/dev/null || echo "NotFound")
  log "  状態: ${STATE} (${i}/20)"
  if [[ "${STATE}" == "Succeeded" ]]; then
    break
  fi
  sleep 15
done

LOG_ANALYTICS_ID=$(az monitor log-analytics workspace show \
  --resource-group "${RESOURCE_GROUP}" \
  --workspace-name "${LOG_ANALYTICS_NAME}" \
  --query id --output tsv)

# ---------------------------------------------------------------
# 4. Application Insights 作成
# ---------------------------------------------------------------
log "=== Application Insights 作成: ${APP_INSIGHTS_NAME} ==="
az monitor app-insights component create \
  --resource-group "${RESOURCE_GROUP}" \
  --app "${APP_INSIGHTS_NAME}" \
  --location "${LOCATION}" \
  --kind web \
  --application-type web \
  --workspace "${LOG_ANALYTICS_ID}" \
  --output none 2>/dev/null || \
az monitor app-insights component update \
  --resource-group "${RESOURCE_GROUP}" \
  --app "${APP_INSIGHTS_NAME}" \
  --workspace "${LOG_ANALYTICS_ID}" \
  --output none

CONNECTION_STRING=$(az monitor app-insights component show \
  --resource-group "${RESOURCE_GROUP}" \
  --app "${APP_INSIGHTS_NAME}" \
  --query connectionString --output tsv)

log "接続文字列取得完了"

# ---------------------------------------------------------------
# 5. kubectl 認証情報の取得
# ---------------------------------------------------------------
log "=== kubectl 認証情報を取得 ==="
az aks get-credentials \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --overwrite-existing

# ---------------------------------------------------------------
# 6. Application Insights 接続文字列を K8s Secret に登録
# ---------------------------------------------------------------
log "=== K8s Secret に接続文字列を登録 ==="
kubectl create secret generic appinsights-secret \
  --from-literal=connection-string="${CONNECTION_STRING}" \
  --dry-run=client -o yaml | kubectl apply -f -

# ---------------------------------------------------------------
# 7. OTel Collector をデプロイ
# ---------------------------------------------------------------
log "=== OTel Collector をデプロイ ==="
kubectl apply -f "${REPO_ROOT}/k8s/otel-collector.yaml"
kubectl rollout status deployment/otel-collector --timeout=3m

# ---------------------------------------------------------------
# 8. 各エージェントを OTel Collector エンドポイントで再デプロイ
# ---------------------------------------------------------------
log "=== 各エージェントのマニフェストを再適用 ==="
ACR_LOGIN_SERVER=$(az acr show \
  --name "${ACR_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --query loginServer --output tsv)

MANIFEST_TMP="$(mktemp /tmp/aks-infrastructure-XXXXXX.yaml)"
sed "s|<ACR_NAME>|${ACR_LOGIN_SERVER}|g" \
  "${REPO_ROOT}/A2ADispatcher/aks-infrastructure.yaml" > "${MANIFEST_TMP}"
kubectl apply -f "${MANIFEST_TMP}"
rm -f "${MANIFEST_TMP}"

kubectl rollout restart deployment/a2a-dispatcher
kubectl rollout restart deployment/simple-agent
kubectl rollout restart deployment/echo-agent
kubectl rollout restart deployment/agent-card-viewer

log "=== Pod 起動を待機 ==="
kubectl rollout status deployment/a2a-dispatcher    --timeout=3m
kubectl rollout status deployment/simple-agent      --timeout=3m
kubectl rollout status deployment/echo-agent        --timeout=3m
kubectl rollout status deployment/agent-card-viewer --timeout=3m

# ---------------------------------------------------------------
# 9. 完了サマリー
# ---------------------------------------------------------------
PORTAL_URL="https://portal.azure.com/#resource$(az monitor app-insights component show \
  --resource-group "${RESOURCE_GROUP}" \
  --app "${APP_INSIGHTS_NAME}" \
  --query id --output tsv)/overview"

log ""
log "======================================================"
log " OTel → Application Insights セットアップ完了！"
log ""
log " Azure Portal:"
log "   ${PORTAL_URL}"
log ""
log " トレース確認（Transaction Search）:"
log "   ポータル → Application Insights → 調査 → トランザクション検索"
log ""
log " ライブメトリクス（リアルタイム）:"
log "   ポータル → Application Insights → 調査 → ライブメトリクス"
log ""
log " テストリクエスト:"
DISPATCHER_IP=$(kubectl get svc a2a-dispatcher-svc \
  -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>/dev/null || echo "<pending>")
log '   curl -X POST "http://'"${DISPATCHER_IP}"'/agent" \'
log '     -H "Content-Type: application/json" \'
log "     -d '{\"requiredCapability\": \"サンプル .NET エージェント\", \"message\": \"こんにちは\"}'"
log "======================================================"
