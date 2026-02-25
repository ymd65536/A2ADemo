#!/usr/bin/env bash
# =============================================================
# setup-keyvault.sh
# Azure Key Vault + Secrets Store CSI Driver + Workload Identity を使って
# Application Insights 接続文字列を安全に AKS へ渡す
#
# 仕組み:
#   Key Vault (接続文字列を保管)
#     ↓ Secrets Store CSI Driver (Key Vault Provider)
#   SecretProviderClass → K8s Secret に同期
#     ↓ secretKeyRef
#   OTel Collector Pod (環境変数として注入)
# =============================================================
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-a2a-demo-rg}"
LOCATION="${LOCATION:-japaneast}"
AKS_CLUSTER="${AKS_CLUSTER:-a2a-demo-aks}"
ACR_NAME="${ACR_NAME:-myuniquacr}"
APP_INSIGHTS_NAME="${APP_INSIGHTS_NAME:-a2a-demo-appinsights}"
KEY_VAULT_NAME="${KEY_VAULT_NAME:-a2a-demo-kv}"      # グローバル一意名（3-24 文字）
IDENTITY_NAME="${IDENTITY_NAME:-a2a-otel-identity}"
KV_SECRET_NAME="appinsights-connection-string"
K8S_NAMESPACE="${K8S_NAMESPACE:-default}"
K8S_SERVICE_ACCOUNT="otel-collector-sa"

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

log() { echo "[$(date '+%H:%M:%S')] $*"; }

# ---------------------------------------------------------------
# 1. Azure ログイン確認
# ---------------------------------------------------------------
log "=== Azure ログイン確認 ==="
if ! az account show &>/dev/null; then
  az login
fi
SUBSCRIPTION_ID=$(az account show --query id --output tsv)
TENANT_ID=$(az account show --query tenantId --output tsv)
log "Subscription: ${SUBSCRIPTION_ID}"
log "Tenant:       ${TENANT_ID}"

# ---------------------------------------------------------------
# 2. Key Vault プロバイダー登録
# ---------------------------------------------------------------
log "=== リソースプロバイダー登録 ==="
az provider register --namespace Microsoft.KeyVault --wait

# ---------------------------------------------------------------
# 3. Key Vault 作成
# ---------------------------------------------------------------
log "=== Key Vault 作成: ${KEY_VAULT_NAME} ==="
az keyvault create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${KEY_VAULT_NAME}" \
  --location "${LOCATION}" \
  --enable-rbac-authorization true \
  --output none

KEY_VAULT_ID=$(az keyvault show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${KEY_VAULT_NAME}" \
  --query id --output tsv)
log "Key Vault ID: ${KEY_VAULT_ID}"

# ---------------------------------------------------------------
# 4. Application Insights 接続文字列を Key Vault に保存
# ---------------------------------------------------------------
log "=== Application Insights 接続文字列を Key Vault に登録 ==="
CONNECTION_STRING=$(az monitor app-insights component show \
  --resource-group "${RESOURCE_GROUP}" \
  --app "${APP_INSIGHTS_NAME}" \
  --query connectionString --output tsv)

# 自分自身に Key Vault Secrets Officer ロールを付与してからシークレット登録
CURRENT_USER=$(az ad signed-in-user show --query id --output tsv 2>/dev/null || \
               az account show --query user.name --output tsv)
az role assignment create \
  --assignee "${CURRENT_USER}" \
  --role "Key Vault Secrets Officer" \
  --scope "${KEY_VAULT_ID}" \
  --output none 2>/dev/null || true

# RBAC 伝搬待ち
sleep 15

az keyvault secret set \
  --vault-name "${KEY_VAULT_NAME}" \
  --name "${KV_SECRET_NAME}" \
  --value "${CONNECTION_STRING}" \
  --output none
log "シークレット登録完了: ${KV_SECRET_NAME}"

# ---------------------------------------------------------------
# 5. AKS に OIDC Issuer + Workload Identity + CSI Driver を有効化
# ---------------------------------------------------------------
log "=== AKS アドオンを有効化（OIDC / Workload Identity / Secrets Store CSI）==="
az aks update \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --enable-oidc-issuer \
  --enable-workload-identity \
  --output none

az aks enable-addons \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --addons azure-keyvault-secrets-provider \
  --output none

OIDC_ISSUER=$(az aks show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --query oidcIssuerProfile.issuerUrl --output tsv)
log "OIDC Issuer: ${OIDC_ISSUER}"

# ---------------------------------------------------------------
# 6. User-Assigned Managed Identity 作成
# ---------------------------------------------------------------
log "=== Managed Identity 作成: ${IDENTITY_NAME} ==="
az identity create \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${IDENTITY_NAME}" \
  --output none

IDENTITY_CLIENT_ID=$(az identity show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${IDENTITY_NAME}" \
  --query clientId --output tsv)
IDENTITY_PRINCIPAL_ID=$(az identity show \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${IDENTITY_NAME}" \
  --query principalId --output tsv)
log "Identity Client ID:    ${IDENTITY_CLIENT_ID}"
log "Identity Principal ID: ${IDENTITY_PRINCIPAL_ID}"

# ---------------------------------------------------------------
# 7. Managed Identity に Key Vault Secrets User ロールを付与
# ---------------------------------------------------------------
log "=== Key Vault Secrets User ロールを Managed Identity に付与 ==="
az role assignment create \
  --assignee-object-id "${IDENTITY_PRINCIPAL_ID}" \
  --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" \
  --scope "${KEY_VAULT_ID}" \
  --output none

# ---------------------------------------------------------------
# 8. kubectl 認証情報の取得
# ---------------------------------------------------------------
log "=== kubectl 認証情報を取得 ==="
az aks get-credentials \
  --resource-group "${RESOURCE_GROUP}" \
  --name "${AKS_CLUSTER}" \
  --overwrite-existing

# ---------------------------------------------------------------
# 9. K8s ServiceAccount 作成（Workload Identity アノテーション付き）
# ---------------------------------------------------------------
log "=== K8s ServiceAccount 作成: ${K8S_SERVICE_ACCOUNT} ==="
kubectl create serviceaccount "${K8S_SERVICE_ACCOUNT}" \
  --namespace "${K8S_NAMESPACE}" \
  --dry-run=client -o yaml | \
kubectl annotate --local -f - \
  "azure.workload.identity/client-id=${IDENTITY_CLIENT_ID}" \
  --overwrite -o yaml | \
kubectl apply -f -

# ---------------------------------------------------------------
# 10. Federated Credential 作成（K8s SA ↔ Managed Identity を連携）
# ---------------------------------------------------------------
log "=== Federated Credential 作成 ==="
az identity federated-credential create \
  --name "otel-collector-federated" \
  --identity-name "${IDENTITY_NAME}" \
  --resource-group "${RESOURCE_GROUP}" \
  --issuer "${OIDC_ISSUER}" \
  --subject "system:serviceaccount:${K8S_NAMESPACE}:${K8S_SERVICE_ACCOUNT}" \
  --audience "api://AzureADTokenExchange" \
  --output none

# ---------------------------------------------------------------
# 11. SecretProviderClass を生成して適用
# ---------------------------------------------------------------
log "=== SecretProviderClass を生成・適用 ==="
cat <<EOF | kubectl apply -f -
apiVersion: secrets-store.csi.x-k8s.io/v1
kind: SecretProviderClass
metadata:
  name: appinsights-keyvault
  namespace: ${K8S_NAMESPACE}
spec:
  provider: azure
  parameters:
    usePodIdentity: "false"
    clientID: "${IDENTITY_CLIENT_ID}"
    keyvaultName: "${KEY_VAULT_NAME}"
    tenantId: "${TENANT_ID}"
    objects: |
      array:
        - |
          objectName: ${KV_SECRET_NAME}
          objectType: secret
          objectVersion: ""
  # Key Vault のシークレットを K8s Secret に同期する
  secretObjects:
  - secretName: appinsights-secret
    type: Opaque
    data:
    - objectName: ${KV_SECRET_NAME}
      key: connection-string
EOF

# ---------------------------------------------------------------
# 12. OTel Collector を Key Vault 対応マニフェストで再デプロイ
# ---------------------------------------------------------------
log "=== OTel Collector を再デプロイ（Key Vault 対応版）==="
kubectl apply -f "${REPO_ROOT}/k8s/otel-collector.yaml"
kubectl rollout restart deployment/otel-collector
kubectl rollout status deployment/otel-collector --timeout=5m

# ---------------------------------------------------------------
# 13. 確認
# ---------------------------------------------------------------
log "=== 確認 ==="
kubectl get secretproviderclass appinsights-keyvault -n "${K8S_NAMESPACE}"
kubectl get secret appinsights-secret -n "${K8S_NAMESPACE}" 2>/dev/null && \
  log "K8s Secret が Key Vault から同期されました" || \
  log "※ Secret は Pod 起動後に同期されます（Pod が CSI ボリュームをマウントすると生成）"

log ""
log "======================================================"
log " Key Vault セットアップ完了！"
log ""
log " Key Vault:        ${KEY_VAULT_NAME}"
log " シークレット名:   ${KV_SECRET_NAME}"
log " Managed Identity: ${IDENTITY_NAME} (clientId: ${IDENTITY_CLIENT_ID})"
log ""
log " シークレットの確認:"
log "   az keyvault secret show --vault-name ${KEY_VAULT_NAME} --name ${KV_SECRET_NAME} --query value -o tsv"
log ""
log " K8s Secret の同期確認（Pod 起動後）:"
log "   kubectl get secret appinsights-secret -o jsonpath='{.data.connection-string}' | base64 --decode"
log "======================================================"
