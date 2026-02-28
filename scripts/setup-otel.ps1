# =============================================================
# setup-otel.ps1
# Application Insights を作成し、OTel Collector を AKS へデプロイする
# =============================================================
$ErrorActionPreference = "Stop"

# デフォルト値の設定
if (!$env:RESOURCE_GROUP) { $env:RESOURCE_GROUP = "a2a-demo-rg" }
if (!$env:LOCATION) { $env:LOCATION = "japaneast" }
if (!$env:APP_INSIGHTS_NAME) { $env:APP_INSIGHTS_NAME = "a2a-demo-appinsights" }
if (!$env:AKS_CLUSTER) { $env:AKS_CLUSTER = "a2a-demo-aks" }
if (!$env:ACR_NAME) { $env:ACR_NAME = "myuniquacr" }

$RESOURCE_GROUP = $env:RESOURCE_GROUP
$LOCATION = $env:LOCATION
$APP_INSIGHTS_NAME = $env:APP_INSIGHTS_NAME
$AKS_CLUSTER = $env:AKS_CLUSTER
$ACR_NAME = $env:ACR_NAME

$REPO_ROOT = Split-Path -Parent $PSScriptRoot

function Log {
    param([string]$Message)
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message"
}

# ---------------------------------------------------------------
# 1. Azure ログイン確認
# ---------------------------------------------------------------
Log "=== Azure ログイン確認 ==="
try {
    az account show --output none 2>$null
}
catch {
    az login
}

# ---------------------------------------------------------------
# 2. Application Insights 用リソースプロバイダー登録
# ---------------------------------------------------------------
Log "=== リソースプロバイダー登録 ==="
az provider register --namespace microsoft.insights --wait
az provider register --namespace microsoft.operationalinsights --wait

# ---------------------------------------------------------------
# 3. Log Analytics ワークスペース作成（Application Insights の背後）
# ---------------------------------------------------------------
$LOG_ANALYTICS_NAME = "${APP_INSIGHTS_NAME}-laws"
Log "=== Log Analytics ワークスペース作成（既存の場合はスキップ）: ${LOG_ANALYTICS_NAME} ==="
try {
    az monitor log-analytics workspace create `
        --resource-group $RESOURCE_GROUP `
        --workspace-name $LOG_ANALYTICS_NAME `
        --location $LOCATION `
        --output none 2>$null
}
catch {
    # 既存の場合はスキップ
}

# プロビジョニング完了まで待機
Log "=== Log Analytics ワークスペースのプロビジョニング完了を待機 ==="
$STATE = "NotFound"
for ($i = 1; $i -le 20; $i++) {
    try {
        $STATE = az monitor log-analytics workspace show `
            --resource-group $RESOURCE_GROUP `
            --workspace-name $LOG_ANALYTICS_NAME `
            --query provisioningState --output tsv 2>$null
    }
    catch {
        $STATE = "NotFound"
    }
    Log "  状態: ${STATE} (${i}/20)"
    if ($STATE -eq "Succeeded") {
        break
    }
    Start-Sleep -Seconds 15
}

$LOG_ANALYTICS_ID = az monitor log-analytics workspace show `
    --resource-group $RESOURCE_GROUP `
    --workspace-name $LOG_ANALYTICS_NAME `
    --query id --output tsv

# ---------------------------------------------------------------
# 4. Application Insights 作成
# ---------------------------------------------------------------
Log "=== Application Insights 作成: ${APP_INSIGHTS_NAME} ==="
try {
    az monitor app-insights component create `
        --resource-group $RESOURCE_GROUP `
        --app $APP_INSIGHTS_NAME `
        --location $LOCATION `
        --kind web `
        --application-type web `
        --workspace $LOG_ANALYTICS_ID `
        --output none 2>$null
}
catch {
    az monitor app-insights component update `
        --resource-group $RESOURCE_GROUP `
        --app $APP_INSIGHTS_NAME `
        --workspace $LOG_ANALYTICS_ID `
        --output none
}

$CONNECTION_STRING = az monitor app-insights component show `
    --resource-group $RESOURCE_GROUP `
    --app $APP_INSIGHTS_NAME `
    --query connectionString --output tsv

Log "接続文字列取得完了"

# ---------------------------------------------------------------
# 5. kubectl 認証情報の取得
# ---------------------------------------------------------------
Log "=== kubectl 認証情報を取得 ==="
az aks get-credentials `
    --resource-group $RESOURCE_GROUP `
    --name $AKS_CLUSTER `
    --overwrite-existing

# ---------------------------------------------------------------
# 6. Application Insights 接続文字列を K8s Secret に登録
# ---------------------------------------------------------------
Log "=== K8s Secret に接続文字列を登録 ==="
kubectl create secret generic appinsights-secret `
    --from-literal=connection-string="$CONNECTION_STRING" `
    --dry-run=client -o yaml | kubectl apply -f -

# ---------------------------------------------------------------
# 7. OTel Collector をデプロイ
# ---------------------------------------------------------------
Log "=== OTel Collector をデプロイ ==="
kubectl apply -f "$REPO_ROOT/k8s/otel-collector.yaml"
kubectl rollout status deployment/otel-collector --timeout=3m

# ---------------------------------------------------------------
# 8. 各エージェントを OTel Collector エンドポイントで再デプロイ
# ---------------------------------------------------------------
Log "=== 各エージェントのマニフェストを再適用 ==="
$ACR_LOGIN_SERVER = az acr show `
    --name $ACR_NAME `
    --resource-group $RESOURCE_GROUP `
    --query loginServer --output tsv

$MANIFEST_TMP = New-TemporaryFile
$manifestContent = Get-Content "$REPO_ROOT/A2ADispatcher/aks-infrastructure.yaml" -Raw
$manifestContent = $manifestContent -replace '<ACR_NAME>', $ACR_LOGIN_SERVER
$manifestContent | Set-Content -Path $MANIFEST_TMP.FullName -Encoding UTF8

kubectl apply -f $MANIFEST_TMP.FullName
Remove-Item $MANIFEST_TMP.FullName

kubectl rollout restart deployment/a2a-dispatcher
kubectl rollout restart deployment/simple-agent
kubectl rollout restart deployment/echo-agent
kubectl rollout restart deployment/agent-card-viewer

Log "=== Pod 起動を待機 ==="
kubectl rollout status deployment/a2a-dispatcher    --timeout=3m
kubectl rollout status deployment/simple-agent      --timeout=3m
kubectl rollout status deployment/echo-agent        --timeout=3m
kubectl rollout status deployment/agent-card-viewer --timeout=3m

# ---------------------------------------------------------------
# 9. 完了サマリー
# ---------------------------------------------------------------
$APP_INSIGHTS_ID = az monitor app-insights component show `
    --resource-group $RESOURCE_GROUP `
    --app $APP_INSIGHTS_NAME `
    --query id --output tsv

$PORTAL_URL = "https://portal.azure.com/#resource${APP_INSIGHTS_ID}/overview"

Log ""
Log "======================================================"
Log " OTel → Application Insights セットアップ完了！"
Log ""
Log " Azure Portal:"
Log "   ${PORTAL_URL}"
Log ""
Log " トレース確認（Transaction Search）:"
Log "   ポータル → Application Insights → 調査 → トランザクション検索"
Log ""
Log " ライブメトリクス（リアルタイム）:"
Log "   ポータル → Application Insights → 調査 → ライブメトリクス"
Log ""
Log " テストリクエスト:"

try {
    $DISPATCHER_IP = kubectl get svc a2a-dispatcher-svc `
        -o jsonpath='{.status.loadBalancer.ingress[0].ip}' 2>$null
    if (!$DISPATCHER_IP) { $DISPATCHER_IP = "<pending>" }
}
catch {
    $DISPATCHER_IP = "<pending>"
}

Log "   curl -X POST `"http://${DISPATCHER_IP}/agent`" \"
Log "     -H `"Content-Type: application/json`" \"
Log "     -d '{`"requiredCapability`": `"サンプル .NET エージェント`", `"message`": `"こんにちは`"}'"
Log "======================================================"
