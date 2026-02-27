#!/usr/bin/env bash
# =============================================================
# deploy-to-gke.sh
# A2ADispatcher を Google Kubernetes Engine にデプロイするスクリプト
# =============================================================
set -euo pipefail

# ---------------------------------------------------------------
# Python 互換性の保証
#   gcloud SDK は Python 3.12 以降（imp モジュール廃止）で動作しない古いバージョンがある。
#   Homebrew 版 gcloud は内部 venv を使うため通常は不要だが、
#   万が一 CLOUDSDK_PYTHON が未設定の場合に Python 3.11 を使用するフォールバック。
# ---------------------------------------------------------------
if ! gcloud version &>/dev/null 2>&1; then
  for py in python3.11 python3.10; do
    _py_path="$(command -v "${py}" 2>/dev/null || true)"
    if [[ -n "${_py_path}" ]]; then
      export CLOUDSDK_PYTHON="${_py_path}"
      echo "[INFO] CLOUDSDK_PYTHON を ${_py_path} に設定しました"
      break
    fi
  done
fi

# ---------------------------------------------------------------
# 変数定義（必要に応じて変更してください）
# ---------------------------------------------------------------
GKE_CLUSTER="${GKE_CLUSTER:-a2a-demo-gke}"
GKE_REGION="${GKE_REGION:-asia-northeast1}"      # リージョンクラスター推奨
GKE_ZONE="${GKE_ZONE:-asia-northeast1-a}"        # ゾーンクラスター使用時のみ
GAR_REGION="${GAR_REGION:-asia-northeast1}"       # Artifact Registry リージョン
GAR_REPO_NAME="${GAR_REPO_NAME:-a2a-demo}"        # Artifact Registry リポジトリ名
NODE_COUNT="${NODE_COUNT:-2}"
# e2-medium は e2-standard-2 より在庫が確保しやすく、デモ用途に十分
NODE_MACHINE_TYPE="${NODE_MACHINE_TYPE:-e2-medium}"
# スポットインスタンス: 在庫不足を回避しやすく、コストも約 60〜90% 削減（true/false）
USE_SPOT="${USE_SPOT:-true}"
# ディスクタイプ: pd-balanced（SSD）を使用
# SSD クォータ不足の場合は DISK_SIZE を削減して対応する
DISK_TYPE="${DISK_TYPE:-pd-balanced}"
# ディスクサイズ: 30GB に削減して SSD_TOTAL_GB クォータ消費を最小化
# (デフォルト 100GB × ノード数 分が SSD クォータを消費する)
DISK_SIZE="${DISK_SIZE:-30}"
NAMESPACE="${NAMESPACE:-a2a-demo}"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DISPATCHER_DIR="${REPO_ROOT}/A2ADispatcher"

# ---------------------------------------------------------------
# 関数
# ---------------------------------------------------------------
log() { echo "[$(date '+%H:%M:%S')] $*"; }

# ---------------------------------------------------------------
# 1. gcloud プロジェクト・ログイン確認
# ---------------------------------------------------------------
log "=== GCP ログイン確認 ==="
if ! gcloud auth print-access-token &>/dev/null; then
  gcloud auth login
fi

# gcloud の現在の設定からプロジェクト ID を取得する
GCP_PROJECT_ID="$(gcloud config get-value project 2>/dev/null || true)"
if [[ -z "${GCP_PROJECT_ID}" ]]; then
  echo "[ERROR] GCP プロジェクト ID を取得できませんでした。"
  echo "  以下のコマンドでプロジェクトを設定してください:"
  echo "    gcloud config set project <PROJECT_ID>"
  exit 1
fi
log "gcloud config からプロジェクト ID を取得しました: ${GCP_PROJECT_ID}"

# Artifact Registry のホスト名（イメージ参照に使用）
GAR_HOST="${GAR_REGION}-docker.pkg.dev"
GAR_PREFIX="${GAR_HOST}/${GCP_PROJECT_ID}/${GAR_REPO_NAME}"

log "GCP プロジェクト: ${GCP_PROJECT_ID}"

# ---------------------------------------------------------------
# 2. 必要な API の有効化
# ---------------------------------------------------------------
log "=== 必要な API を有効化 ==="
gcloud services enable \
  container.googleapis.com \
  artifactregistry.googleapis.com \
  cloudbuild.googleapis.com \
  --project "${GCP_PROJECT_ID}"
log "API 有効化完了"

# ---------------------------------------------------------------
# 3. Artifact Registry リポジトリ作成
# ---------------------------------------------------------------
log "=== Artifact Registry リポジトリ作成: ${GAR_REPO_NAME} ==="
if ! gcloud artifacts repositories describe "${GAR_REPO_NAME}" \
    --location="${GAR_REGION}" \
    --project="${GCP_PROJECT_ID}" &>/dev/null; then
  gcloud artifacts repositories create "${GAR_REPO_NAME}" \
    --repository-format=docker \
    --location="${GAR_REGION}" \
    --project="${GCP_PROJECT_ID}" \
    --description="A2ADemo コンテナイメージリポジトリ"
  log "リポジトリ作成完了: ${GAR_REPO_NAME}"
else
  log "リポジトリは既に存在します: ${GAR_REPO_NAME}"
fi

# ---------------------------------------------------------------
# 4. Docker 認証情報の設定（Artifact Registry へのプッシュに必要）
# ---------------------------------------------------------------
log "=== Docker 認証情報の設定 ==="
gcloud auth configure-docker "${GAR_HOST}" --quiet

# ---------------------------------------------------------------
# 5. Cloud Build でイメージをビルド & プッシュ
#    gcloud builds submit はビルドコンテキストを GCS へ送信し、
#    Cloud Build 上でビルド・プッシュまで完結させます。
#    FORCE_BUILD=true を指定すると既存イメージがあっても強制再ビルドします。
# ---------------------------------------------------------------
FORCE_BUILD="${FORCE_BUILD:-false}"

cloudbuild() {
  local name="$1"
  local context="$2"
  local dockerfile="${3:-dockerfile}"
  local image="${GAR_PREFIX}/${name}:latest"

  # イメージが既に存在する場合はスキップ（FORCE_BUILD=true で強制再ビルド）
  if [[ "${FORCE_BUILD}" != "true" ]] && \
     gcloud artifacts docker images describe "${image}" \
       --project="${GCP_PROJECT_ID}" &>/dev/null; then
    log "スキップ (既存イメージあり): ${image}"
    log "  強制再ビルドする場合: export FORCE_BUILD=true"
    return 0
  fi

  local config_tmp
  config_tmp="$(mktemp /tmp/cloudbuild-XXXXXX)"

  # gcloud builds submit は --dockerfile フラグを持たないため、
  # インライン cloudbuild.yaml を生成して --config で渡す
  cat > "${config_tmp}" <<CLOUDBUILD
steps:
- name: 'gcr.io/cloud-builders/docker'
  args: ['build', '-t', '${image}', '-f', '${dockerfile}', '.']
images:
- '${image}'
CLOUDBUILD

  log "Cloud Build 開始: ${image} (context: ${context})"
  gcloud builds submit \
    --project="${GCP_PROJECT_ID}" \
    --config="${config_tmp}" \
    "${context}"
  rm -f "${config_tmp}"
  log "Cloud Build 完了: ${image}"
}

cloudbuild "a2a-dispatcher"        "${DISPATCHER_DIR}/Dispatcher"
cloudbuild "a2a-simple-agent"      "${DISPATCHER_DIR}/SimpleAgent"
cloudbuild "a2a-agent-card-viewer" "${DISPATCHER_DIR}/AgentCardViewer"
cloudbuild "a2a-echo-agent"        "${DISPATCHER_DIR}/EchoAgent"

# ---------------------------------------------------------------
# 6. GKE クラスター作成
# ---------------------------------------------------------------
log "=== GKE クラスター作成: ${GKE_CLUSTER} (約 3〜5 分かかります) ==="

# 失敗状態のクラスターが残っている場合は削除してからリトライする
if gcloud container clusters describe "${GKE_CLUSTER}" \
    --region="${GKE_REGION}" \
    --project="${GCP_PROJECT_ID}" &>/dev/null; then
  CLUSTER_STATUS="$(gcloud container clusters describe "${GKE_CLUSTER}" \
    --region="${GKE_REGION}" \
    --project="${GCP_PROJECT_ID}" \
    --format='value(status)')"
  if [[ "${CLUSTER_STATUS}" == "RUNNING" ]]; then
    log "GKE クラスターは既に存在します (RUNNING): ${GKE_CLUSTER}"
  else
    log "[WARN] クラスターが ${CLUSTER_STATUS} 状態です。削除して再作成します..."
    gcloud container clusters delete "${GKE_CLUSTER}" \
      --region="${GKE_REGION}" \
      --project="${GCP_PROJECT_ID}" \
      --quiet
    log "クラスター削除完了"
  fi
fi

if ! gcloud container clusters describe "${GKE_CLUSTER}" \
    --region="${GKE_REGION}" \
    --project="${GCP_PROJECT_ID}" &>/dev/null; then

  # スポットインスタンスフラグの設定（在庫不足を回避しやすい）
  SPOT_FLAG=""
  if [[ "${USE_SPOT}" == "true" ]]; then
    SPOT_FLAG="--spot"
    log "スポットインスタンスを使用します (USE_SPOT=true)"
  fi

  log "ディスクタイプ: ${DISK_TYPE} / サイズ: ${DISK_SIZE}GB"

  if ! gcloud container clusters create "${GKE_CLUSTER}" \
    --project="${GCP_PROJECT_ID}" \
    --region="${GKE_REGION}" \
    --num-nodes="${NODE_COUNT}" \
    --machine-type="${NODE_MACHINE_TYPE}" \
    --disk-type="${DISK_TYPE}" \
    --disk-size="${DISK_SIZE}" \
    --workload-pool="${GCP_PROJECT_ID}.svc.id.goog" \
    --enable-autoscaling \
    --min-nodes=1 \
    --max-nodes=5 \
    --release-channel=regular \
    ${SPOT_FLAG}; then
    echo ""
    echo "[ERROR] GKE クラスター作成に失敗しました。"
    echo "  GCE_STOCKOUT / GCE_QUOTA_EXCEEDED の場合は以下をお試しください:"
    echo ""
    echo "  1. SSD クォータ超過の場合 → pd-standard（デフォルト）が設定済みのため"
    echo "     ディスクサイズをさらに削減:"
    echo "       export DISK_SIZE=30"
    echo "       ./scripts/deploy-to-gke.sh"
    echo ""
    echo "  2. 別のマシンタイプを使用:"
    echo "       export NODE_MACHINE_TYPE=e2-small"
    echo "       ./scripts/deploy-to-gke.sh"
    echo ""
    echo "  ※ 削除済みマシンのクォータが残っている場合は数分待ってから再実行してください"
    echo "       ./scripts/deploy-to-gke.sh"
    echo ""
    echo "  2. 別のリージョンを使用（asia-northeast2 = 大阪）:"
    echo "       export GKE_REGION=asia-northeast2"
    echo "       export GAR_REGION=asia-northeast2"
    echo "       ./scripts/deploy-to-gke.sh"
    echo ""
    echo "  3. スポットインスタンスをオフにしてリトライ（既に true の場合は off で試す）:"
    echo "       export USE_SPOT=false"
    echo "       ./scripts/deploy-to-gke.sh"
    # 不完全なクラスターを削除してクリーンな状態に戻す
    gcloud container clusters delete "${GKE_CLUSTER}" \
      --region="${GKE_REGION}" \
      --project="${GCP_PROJECT_ID}" \
      --quiet 2>/dev/null || true
    exit 1
  fi
  log "GKE クラスター作成完了"
fi

# ---------------------------------------------------------------
# 7. GKE ノードへの Artifact Registry 読み取り権限付与
# ---------------------------------------------------------------
log "=== GKE ノードサービスアカウントへの GAR 権限付与 ==="
GKE_SA_EMAIL="$(gcloud container clusters describe "${GKE_CLUSTER}" \
  --region="${GKE_REGION}" \
  --project="${GCP_PROJECT_ID}" \
  --format='value(nodeConfig.serviceAccount)')"

if [[ "${GKE_SA_EMAIL}" == "default" ]]; then
  # default コンピュートサービスアカウントを使用
  GKE_SA_EMAIL="$(gcloud iam service-accounts list \
    --project="${GCP_PROJECT_ID}" \
    --filter="displayName:Compute Engine default service account" \
    --format='value(email)')"
fi

gcloud projects add-iam-policy-binding "${GCP_PROJECT_ID}" \
  --member="serviceAccount:${GKE_SA_EMAIL}" \
  --role="roles/artifactregistry.reader" \
  --condition=None \
  --quiet
log "GAR 読み取り権限付与完了: ${GKE_SA_EMAIL}"

# ---------------------------------------------------------------
# 8. gke-gcloud-auth-plugin の確認とインストール
# ---------------------------------------------------------------
if ! command -v gke-gcloud-auth-plugin &>/dev/null; then
  log "=== gke-gcloud-auth-plugin をインストール ==="
  gcloud components install gke-gcloud-auth-plugin --quiet
else
  log "gke-gcloud-auth-plugin は既にインストール済みです"
fi
export USE_GKE_GCLOUD_AUTH_PLUGIN=True

# ---------------------------------------------------------------
# 9. kubectl 認証情報の取得
# ---------------------------------------------------------------
log "=== kubectl 認証情報を取得 ==="
gcloud container clusters get-credentials "${GKE_CLUSTER}" \
  --region="${GKE_REGION}" \
  --project="${GCP_PROJECT_ID}"
kubectl config current-context

# ---------------------------------------------------------------
# 10. OTel 用 GCP サービスアカウントの作成と Workload Identity 設定
# ---------------------------------------------------------------
OTEL_GCP_SA="a2a-otel-sa"
OTEL_GCP_SA_EMAIL="${OTEL_GCP_SA}@${GCP_PROJECT_ID}.iam.gserviceaccount.com"

log "=== OTel GCP サービスアカウントを作成 ==="
if ! gcloud iam service-accounts describe "${OTEL_GCP_SA_EMAIL}" \
    --project="${GCP_PROJECT_ID}" &>/dev/null; then
  gcloud iam service-accounts create "${OTEL_GCP_SA}" \
    --project="${GCP_PROJECT_ID}" \
    --display-name="A2ADemo OTel Collector"
  log "GCP SA 作成完了: ${OTEL_GCP_SA_EMAIL}"
else
  log "GCP SA は既に存在します: ${OTEL_GCP_SA_EMAIL}"
fi

# Cloud Trace と Cloud Monitoring への書き込み権限を付与
for role in roles/cloudtrace.agent roles/monitoring.metricWriter; do
  gcloud projects add-iam-policy-binding "${GCP_PROJECT_ID}" \
    --member="serviceAccount:${OTEL_GCP_SA_EMAIL}" \
    --role="${role}" \
    --condition=None \
    --quiet
done
log "Cloud Trace / Monitoring 権限付与完了"

# Workload Identity バインド（KSA a2a-demo/a2a-otel-sa ↔ GCP SA）
gcloud iam service-accounts add-iam-policy-binding "${OTEL_GCP_SA_EMAIL}" \
  --project="${GCP_PROJECT_ID}" \
  --role="roles/iam.workloadIdentityUser" \
  --member="serviceAccount:${GCP_PROJECT_ID}.svc.id.goog[${NAMESPACE}/${OTEL_GCP_SA}]" \
  --quiet
log "Workload Identity バインド完了: ${NAMESPACE}/${OTEL_GCP_SA} -> ${OTEL_GCP_SA_EMAIL}"

# ---------------------------------------------------------------
# 11. Kubernetes マニフェストのプレースホルダーを置換してデプロイ
# ---------------------------------------------------------------
log "=== Kubernetes マニフェストを GKE へ適用 ==="
MANIFEST="${DISPATCHER_DIR}/gke-infrastructure.yaml"
MANIFEST_TMP="$(mktemp /tmp/gke-infrastructure-XXXXXX)"

sed \
  -e "s|<GAR_REGION>|${GAR_REGION}|g" \
  -e "s|<GCP_PROJECT_ID>|${GCP_PROJECT_ID}|g" \
  -e "s|<GAR_REPO_NAME>|${GAR_REPO_NAME}|g" \
  "${MANIFEST}" > "${MANIFEST_TMP}"

kubectl apply -f "${MANIFEST_TMP}"
rm -f "${MANIFEST_TMP}"

# ---------------------------------------------------------------
# 12. デプロイ完了確認
# ---------------------------------------------------------------

# rollout 失敗時に原因を自動表示するヘルパー関数
wait_rollout() {
  local deploy="$1"
  log "Rollout 待機: ${deploy} (最大 5 分)"
  if ! kubectl rollout status deployment/${deploy} -n "${NAMESPACE}" --timeout=5m; then
    log "[ERROR] ${deploy} の起動に失敗しました。詳細を表示します。"
    local app_label
    app_label=$(kubectl get deployment "${deploy}" -n "${NAMESPACE}" \
      -o jsonpath='{.spec.selector.matchLabels.app}' 2>/dev/null || echo "${deploy}")
    echo "--- kubectl get pods ---"
    kubectl get pods -n "${NAMESPACE}" -l "app=${app_label}" -o wide
    echo "--- kubectl describe pod ---"
    kubectl describe pod -n "${NAMESPACE}" -l "app=${app_label}" | tail -50
    echo "--- kubectl logs (直近 50 行) ---"
    kubectl logs -n "${NAMESPACE}" deployment/${deploy} --tail=50 2>/dev/null || true
    echo ""
    echo "よくある原因:"
    echo "  1. Artifact Registry からのイメージ取得失敗 → describe pod で 'ImagePullBackOff' を確認"
    echo "     対処: ノードサービスアカウントに roles/artifactregistry.reader が付与されているか確認"
    echo "       gcloud projects get-iam-policy ${GCP_PROJECT_ID} | grep artifactregistry"
    echo "  2. リソース不足 → kubectl describe node で Allocatable を確認"
    echo "  3. 環境変数 / ConfigMap の設定ミス → kubectl describe pod のイベントを確認"
    return 1
  fi
}

log "=== Pod 起動を待機 ==="
wait_rollout otel-collector
wait_rollout a2a-dispatcher
wait_rollout simple-agent
wait_rollout echo-agent
wait_rollout agent-card-viewer

log "=== 稼働中の Pod 一覧 ==="
kubectl get pods -n "${NAMESPACE}" -o wide

log "=== サービス一覧（外部 IP が割り当てられるまで数分かかる場合があります）==="
kubectl get services -n "${NAMESPACE}"

log ""
log "======================================================"
log " デプロイ完了！"
log ""
log " Dispatcher の外部 IP を確認:"
log "   kubectl get svc a2a-dispatcher-svc -n ${NAMESPACE}"
log ""
log " Agent Card Viewer の外部 IP を確認:"
log "   kubectl get svc agent-card-viewer-svc -n ${NAMESPACE}"
log ""
log " Cloud Trace でトレースを確認:"
log "   https://console.cloud.google.com/traces/list?project=${GCP_PROJECT_ID}"
log ""
log " 動作確認:"
log '   DISPATCHER_IP=$(kubectl get svc a2a-dispatcher-svc -n '"${NAMESPACE}"' -o jsonpath="{.status.loadBalancer.ingress[0].ip}")'
log '   curl -X POST "http://${DISPATCHER_IP}/agent" \'
log '     -H "Content-Type: application/json" \'
log "     -d '{\"requiredCapability\": \"サンプル .NET エージェント\", \"message\": \"こんにちは\"}'"
log "======================================================"
