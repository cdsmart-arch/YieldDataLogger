<#
.SYNOPSIS
    One-time script to create all Azure resources for YieldDataLogger and print the
    values you need to add as GitHub Actions secrets.

.DESCRIPTION
    Run this ONCE from your local machine (you don't need to commit the output).
    Requires: az CLI (https://aka.ms/install-az), logged in with: az login

    Edit the $Config block below to match your naming preferences and region, then run:
        pwsh infra/azure-bootstrap.ps1

    The script is idempotent - re-running skips things that already exist.
#>

# ---------------------------------------------------------------------------
# EDIT THESE - everything else is derived.
# ---------------------------------------------------------------------------
$Config = @{
    # Choose a short prefix (lowercase letters/numbers only, max ~8 chars).
    # All resource names are built from this so they're easy to find in the portal.
    Prefix              = "ydlprod"

    # Azure region (az account list-locations -o table)
    Location            = "eastus"

    # Your existing storage account that already has the PriceTicks table
    # (from your GEX calculator setup). Leave blank to create a new one.
    ExistingStorageAccount = "yielddataloggstorage"   # or "" to create a new one

    # Resource group - will be created if it doesn't exist.
    ResourceGroup       = "rg-yielddatalogger"

    # GitHub repo in "owner/repo" format - used to scope the service principal.
    GitHubRepo          = "cdsma/YieldDataLogger"
}
# ---------------------------------------------------------------------------

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-Secret([string]$name, [string]$value) {
    Write-Host ("  {0,-45} = {1}" -f $name, $value) -ForegroundColor Yellow
}

# Check az is available
if (-not (Get-Command az -EA SilentlyContinue)) {
    throw "Azure CLI not found. Install from https://aka.ms/install-az and run 'az login'."
}

$sub = (az account show --query "{id:id,name:name}" -o json | ConvertFrom-Json)
Write-Host "Using subscription: $($sub.name) ($($sub.id))" -ForegroundColor Green

# ---------------------------------------------------------------------------
Write-Step "Resource group"
$rgExists = az group exists --name $Config.ResourceGroup | ConvertFrom-Json
if (-not $rgExists) {
    az group create --name $Config.ResourceGroup --location $Config.Location | Out-Null
    Write-Host "  Created: $($Config.ResourceGroup)"
} else {
    Write-Host "  Already exists: $($Config.ResourceGroup)"
}

# ---------------------------------------------------------------------------
Write-Step "Azure Container Registry"
$acrName = "$($Config.Prefix)acr"
$acrExists = az acr show --name $acrName --resource-group $Config.ResourceGroup --query name -o tsv 2>$null
if (-not $acrExists) {
    az acr create --name $acrName --resource-group $Config.ResourceGroup `
        --sku Basic --admin-enabled true --location $Config.Location | Out-Null
    Write-Host "  Created: $acrName"
} else {
    Write-Host "  Already exists: $acrName"
}
$acrServer   = (az acr show --name $acrName --query loginServer -o tsv)
$acrCreds    = (az acr credential show --name $acrName -o json | ConvertFrom-Json)
$acrUser     = $acrCreds.username
$acrPassword = $acrCreds.passwords[0].value

# ---------------------------------------------------------------------------
Write-Step "Storage account + PriceTicks table"
if ($Config.ExistingStorageAccount) {
    $storageAccount = $Config.ExistingStorageAccount
    Write-Host "  Using existing: $storageAccount"
} else {
    $storageAccount = "$($Config.Prefix)stor"
    $storExists = az storage account show --name $storageAccount --resource-group $Config.ResourceGroup --query name -o tsv 2>$null
    if (-not $storExists) {
        az storage account create --name $storageAccount --resource-group $Config.ResourceGroup `
            --location $Config.Location --sku Standard_LRS --kind StorageV2 `
            --access-tier Hot | Out-Null
        Write-Host "  Created: $storageAccount"
    } else {
        Write-Host "  Already exists: $storageAccount"
    }
}

$storConnString = (az storage account show-connection-string --name $storageAccount `
    --resource-group $Config.ResourceGroup --query connectionString -o tsv)

# Ensure the PriceTicks table exists.
az storage table create --name PriceTicks --connection-string $storConnString --output none 2>$null
Write-Host "  PriceTicks table ready."

# ---------------------------------------------------------------------------
Write-Step "Container Apps environment"
$envName = "cae-$($Config.Prefix)"
$envExists = az containerapp env show --name $envName --resource-group $Config.ResourceGroup --query name -o tsv 2>$null
if (-not $envExists) {
    # Container Apps environment needs the Log Analytics workspace.
    $lawName = "law-$($Config.Prefix)"
    az monitor log-analytics workspace create --workspace-name $lawName `
        --resource-group $Config.ResourceGroup --location $Config.Location | Out-Null
    $lawId  = (az monitor log-analytics workspace show --workspace-name $lawName `
        --resource-group $Config.ResourceGroup --query customerId -o tsv)
    $lawKey = (az monitor log-analytics workspace get-shared-keys --workspace-name $lawName `
        --resource-group $Config.ResourceGroup --query primarySharedKey -o tsv)

    az containerapp env create --name $envName --resource-group $Config.ResourceGroup `
        --location $Config.Location `
        --logs-workspace-id $lawId --logs-workspace-key $lawKey | Out-Null
    Write-Host "  Created: $envName"
} else {
    Write-Host "  Already exists: $envName"
}

# ---------------------------------------------------------------------------
Write-Step "Container App (yields API)"
$appName = "$($Config.Prefix)-api"
$appExists = az containerapp show --name $appName --resource-group $Config.ResourceGroup --query name -o tsv 2>$null
if (-not $appExists) {
    # Initial deploy with a placeholder image; GH Actions will replace it.
    az containerapp create --name $appName --resource-group $Config.ResourceGroup `
        --environment $envName `
        --image "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" `
        --target-port 8080 --ingress external `
        --min-replicas 1 --max-replicas 1 `
        --registry-server $acrServer `
        --registry-username $acrUser `
        --registry-password $acrPassword `
        --secrets "storage-connection-string=$storConnString" `
        --env-vars `
            "ASPNETCORE_ENVIRONMENT=Production" `
            "Storage__Backend=table" `
            "Storage__Tables__Enabled=true" `
            "Storage__Sql__Enabled=false" `
            "Storage__Tables__ConnectionString=secretref:storage-connection-string" | Out-Null
    Write-Host "  Created: $appName"
} else {
    # Update the secret value on existing app in case the key was rotated.
    az containerapp secret set --name $appName --resource-group $Config.ResourceGroup `
        --secrets "storage-connection-string=$storConnString" | Out-Null
    Write-Host "  Updated secret on existing app: $appName"
}

$appFqdn = (az containerapp show --name $appName --resource-group $Config.ResourceGroup `
    --query properties.configuration.ingress.fqdn -o tsv)

# ---------------------------------------------------------------------------
Write-Step "GitHub service principal (AZURE_CREDENTIALS)"
$spName = "sp-github-$($Config.Prefix)"
$scope  = "/subscriptions/$($sub.id)/resourceGroups/$($Config.ResourceGroup)"
$spJson = (az ad sp create-for-rbac --name $spName --role contributor `
    --scopes $scope --sdk-auth -o json 2>$null)

# ---------------------------------------------------------------------------
Write-Host "`n========================================================" -ForegroundColor Green
Write-Host "  ADD THESE AS GITHUB ACTIONS SECRETS" -ForegroundColor Green
Write-Host "  Repo: https://github.com/$($Config.GitHubRepo)/settings/secrets/actions" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Green
Write-Host ""
Write-Secret "AZURE_CREDENTIALS"              "paste the JSON block below"
Write-Host ""
Write-Host $spJson -ForegroundColor DarkYellow
Write-Host ""
Write-Secret "ACR_LOGIN_SERVER"               $acrServer
Write-Secret "ACR_USERNAME"                   $acrUser
Write-Secret "ACR_PASSWORD"                   $acrPassword
Write-Secret "AZURE_RESOURCE_GROUP"           $Config.ResourceGroup
Write-Secret "CONTAINERAPPS_APP_NAME"         $appName
Write-Secret "CONTAINERAPPS_ENVIRONMENT_NAME" $envName
Write-Secret "STORAGE_CONNECTION_STRING"      "(see storage-connection-string secret above - do NOT paste to plain env var)"
Write-Host ""
Write-Host "========================================================" -ForegroundColor Green
Write-Host "  APP URL (update Agent HubUrl and AgentOptions.ApiBaseUrl)" -ForegroundColor Green
Write-Host "  https://$appFqdn" -ForegroundColor White
Write-Host "  Hub: https://$appFqdn/hubs/ticks" -ForegroundColor White
Write-Host "  Health: https://$appFqdn/healthz" -ForegroundColor White
Write-Host "  Admin: https://$appFqdn/admin/" -ForegroundColor White
Write-Host "========================================================" -ForegroundColor Green
