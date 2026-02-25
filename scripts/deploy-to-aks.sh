#!/usr/bin/env bash
# =============================================================
# deploy-to-aks.sh
# A2ADispatcher を Azure Kubernetes Service にデプロイするスクリプト
# =============================================================
set -euo pipefail

# ---------------------------------------------------------------
# 変数定義（必要に応じて変更してください）
# ---------------------------------------------------------------
RESOURCE_GROUP="${RESOURCE_GROUP:-a2a-demo-rg}"
LOCATION="${LOCATION:-japaneast}"
ACR_NAME="${ACR_NAME:-myuniquacr}"             # グローバル一意名（小文字英数字のみ）
AKS_CLUSTER="${AKS_CLUSTER:-a2a-demo-aks}"
NODE_COUNT="${NODE_COUNT:-2}"
NODE_VM="${NODE_VM:-Standard_B2s}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DISPATCHER_DIR="${REPO_ROOT}/A2ADispatcher"

# ---------------------------------------------------------------
# 関数
# ---------------------------------------------------------------
log() { echo "[$(date '+%H:%M:%S')] $*"; }

# ---------------------------------------------------------------
# 1. Azure ログイン確認
# ---------------------------------------------------------------
log "=== Azure ログイン確認 ==="
if ! az account show &>/dev/null; then
  az login
fi
az account show --query "{Subscription:name, ID:id}" -o table

# ---------------------------------------------------------------
# 2. リソースグループ作成
# ---------------------------------------------------------------
log "=== リソースグループ作成: ${RESOURCE_GROUP} ==="
az group create \
  --name "${RESOURCE_GROUP}" \
  --location "${LOCATION}" \
  --output none

# ---------------------------------------------------------------
# 3. Azure Container Registry 作成
# ---------------------------------------------------------------
log "=== ACR 作成: ${ACR_NAME} ==="
az acr create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${ACR_NAME}" \
  --sku Basic \
  --output none

ACR_LOGIN_SERVER=$(az acr show \
  --name "${ACR_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --query loginServer \
  --output tsv)
log "ACR ログインサーバー: ${ACR_LOGIN_SERVER}"

# ---------------------------------------------------------------
# 4. ACR Tasks でイメージをクラウドビルド & プッシュ
#    az acr build はビルドコンテキストを Azure へ送信し、
#    ACR 上でビルド・プッシュまで完結させます。
#    ローカルの Docker デーモンや docker login は不要です。
# ---------------------------------------------------------------
acr_build() {
  local name="$1"
  local context="$2"
  local dockerfile="${3:-dockerfile}"
  log "ACR ビルド開始: ${name}:latest (context: ${context})"
  az acr build \
    --registry "${ACR_NAME}" \
    --image "${name}:latest" \
    --file "${context}/${dockerfile}" \
    "${context}"
  log "ACR ビルド完了: ${name}:latest"
}

acr_build "a2a-dispatcher"        "${DISPATCHER_DIR}/Dispatcher"
acr_build "a2a-simple-agent"      "${DISPATCHER_DIR}/SimpleAgent"
acr_build "a2a-agent-card-viewer" "${DISPATCHER_DIR}/AgentCardViewer"
acr_build "a2a-echo-agent"        "${DISPATCHER_DIR}/EchoAgent"

# ---------------------------------------------------------------
# 5. リソースプロバイダー登録
#    AKS を使うには Microsoft.ContainerService の登録が必要
# ---------------------------------------------------------------
log "=== リソースプロバイダー登録 ==="
az provider register --namespace Microsoft.ContainerService --wait
az provider register --namespace Microsoft.Compute --wait
az provider register --namespace Microsoft.Network --wait
log "リソースプロバイダー登録完了"

# ---------------------------------------------------------------
# 6. AKS クラスター作成
# ---------------------------------------------------------------
log "=== AKS クラスター作成: ${AKS_CLUSTER} (約 3〜5 分かかります) ==="
az aks create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --node-count "${NODE_COUNT}" \
  --node-vm-size "${NODE_VM}" \
  --attach-acr "${ACR_NAME}" \
  --generate-ssh-keys \
  --output none
log "AKS クラスター作成完了"

# ACR アタッチが反映されるまで少し待機（ロールバインディング伝搬に数秒かかることがある）
log "=== ACR 権限の伝搬を待機 (30 秒) ==="
sleep 30

# ---------------------------------------------------------------
# 7. kubectl 認証情報の取得
# ---------------------------------------------------------------
log "=== kubectl 認証情報を取得 ==="
az aks get-credentials \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --overwrite-existing
kubectl config current-context

# ---------------------------------------------------------------
# 8. Kubernetes マニフェストの ACR 名を置換してデプロイ
# ---------------------------------------------------------------
log "=== Kubernetes マニフェストを AKS へ適用 ==="
MANIFEST="${DISPATCHER_DIR}/aks-infrastructure.yaml"
MANIFEST_TMP="$(mktemp /tmp/aks-infrastructure-XXXXXX.yaml)"

sed "s|<ACR_NAME>|${ACR_LOGIN_SERVER}|g" "${MANIFEST}" > "${MANIFEST_TMP}"
kubectl apply -f "${MANIFEST_TMP}"
rm -f "${MANIFEST_TMP}"

# ---------------------------------------------------------------
# 9. デプロイ完了確認
# ---------------------------------------------------------------

# rollout 失敗時に原因を自動表示するヘルパー関数
wait_rollout() {
  local deploy="$1"
  log "Rollout 待機: ${deploy} (最大 5 分)"
  if ! kubectl rollout status deployment/${deploy} --timeout=5m; then
    log "[ERROR] ${deploy} の起動に失敗しました。詳細を表示します。"
    echo "--- kubectl get pods ---"
    kubectl get pods -l "app=$(kubectl get deployment ${deploy} -o jsonpath='{.spec.selector.matchLabels.app}' 2>/dev/null || echo ${deploy})" -o wide
    echo "--- kubectl describe pod ---"
    kubectl describe pod -l "app=$(kubectl get deployment ${deploy} -o jsonpath='{.spec.selector.matchLabels.app}' 2>/dev/null || echo ${deploy})" | tail -40
    echo "--- kubectl logs (直近 50 行) ---"
    kubectl logs deployment/${deploy} --tail=50 2>/dev/null || true
    echo ""
    echo "よくある原因:"
    echo "  1. ACR からのイメージ取得失敗 → kubectl describe pod で 'ImagePullBackOff' を確認"
    echo "     対処: az aks update --resource-group ${RESOURCE_GROUP} --name ${AKS_CLUSTER} --attach-acr ${ACR_NAME}"
    echo "  2. リソース不足 → kubectl describe node で Allocatable を確認"
    echo "  3. 環境変数 / ConfigMap の設定ミス → kubectl describe pod のイベントを確認"
    return 1
  fi
}

log "=== Pod 起動を待機 ==="
wait_rollout a2a-dispatcher
wait_rollout simple-agent
wait_rollout echo-agent
wait_rollout agent-card-viewer

log "=== 稼働中の Pod 一覧 ==="
kubectl get pods -o wide

log "=== サービス一覧（外部 IP が割り当てられるまで数分かかる場合があります）==="
kubectl get services

log ""
log "======================================================"
log " デプロイ完了！"
log ""
log " Dispatcher の外部 IP を確認:"
log "   kubectl get svc a2a-dispatcher-svc"
log ""
log " 動作確認:"
log '   DISPATCHER_IP=$(kubectl get svc a2a-dispatcher-svc -o jsonpath="{.status.loadBalancer.ingress[0].ip}")'
log '   curl -X POST "http://${DISPATCHER_IP}/agent" \'
log '     -H "Content-Type: application/json" \'
log "     -d '{\"requiredCapability\": \"サンプル .NET エージェント\", \"message\": \"こんにちは\"}'"
log "======================================================"
