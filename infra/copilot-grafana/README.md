# GitHub Copilot monitoring with Azure Managed Grafana

This folder creates the Azure side of the monitoring pipeline described in the Microsoft Learn article "Monitor AI coding agents with Grafana" for GitHub Copilot.

## What gets deployed

- Log Analytics workspace
- Workspace-based Application Insights resource
- Azure Managed Grafana 11, Standard SKU
- `Monitoring Reader` role assignments for Grafana's managed identity scoped to the Log Analytics workspace and Application Insights resource

The OpenTelemetry Collector and VS Code GitHub Copilot telemetry settings are intentionally not deployed by Bicep because they run on your developer machine or on whichever shared host receives OTLP traffic.

## Prerequisites

1. Azure subscription where you can create resource groups and role assignments.
2. Azure CLI with Bicep support: `az version` should show a recent CLI and Bicep installed.
3. Docker Desktop or another Docker runtime for the local OpenTelemetry Collector.
4. VS Code with GitHub Copilot Chat.

## Deploy

Pick a short, globally distinctive prefix and set the variables below. The sample `main.bicepparam` keeps defaults for other parameters, while the commands override `namePrefix` and `location` from the script.

```powershell
$resourceGroupName = 'rg-copilot-monitoring'
$location = 'japaneast'
$namePrefix = 'copilotmon'
$namePrefixNormalized = $namePrefix.ToLowerInvariant()

az login
az account set --subscription "<subscription-id-or-name>"
az provider register --namespace Microsoft.Dashboard --wait
az provider show --namespace Microsoft.Dashboard --query registrationState --output tsv
az group create --name $resourceGroupName --location $location
az deployment group what-if `
  --resource-group $resourceGroupName `
  --template-file infra/copilot-grafana/main.bicep `
  --parameters infra/copilot-grafana/main.bicepparam namePrefix=$namePrefix location=$location
az deployment group create `
  --resource-group $resourceGroupName `
  --template-file infra/copilot-grafana/main.bicep `
  --parameters infra/copilot-grafana/main.bicepparam namePrefix=$namePrefix location=$location
```

The deployment output includes the Grafana endpoint. The Application Insights connection string is intentionally not output from the Bicep template, so retrieve it from the Application Insights resource in the collector setup step below. Treat the connection string as a secret because anyone with it can send telemetry into your Application Insights resource.

If you cannot open or administer the Grafana instance after deployment, assign yourself the Azure `Grafana Admin` role on the Managed Grafana resource. Azure Managed Grafana uses Azure RBAC for user access.

```powershell
$assignee = az ad signed-in-user show --query id --output tsv
$grafanaId = az resource show `
  --resource-group $resourceGroupName `
  --resource-type Microsoft.Dashboard/grafana `
  --name "$namePrefixNormalized-grafana" `
  --query id `
  --output tsv
az role assignment create --assignee $assignee --role "Grafana Admin" --scope $grafanaId
```

## Run the OpenTelemetry Collector locally

Use the helper script to create the git-ignored Docker collector config and start the local OpenTelemetry Collector container. The script uses `otel-collector-config.docker.sample.yaml`, writes `artifacts/copilot-grafana/otel-collector-config.yaml`, and publishes OTLP ports only on host `localhost`.

The script pins the OpenTelemetry Collector contrib image to `0.153.0`, which is the version validated with this setup. Update the pinned tag deliberately after testing a newer collector version.

Start the collector with one command. The script resolves the Application Insights connection string from Azure CLI:

```powershell
.\scripts\Start-CopilotTelemetryCollector.ps1 `
  -ResourceGroup $resourceGroupName `
  -AppInsightsName "$namePrefixNormalized-appi"
```

After the config exists, restart the same local collector without re-resolving the connection string:

```powershell
.\scripts\Start-CopilotTelemetryCollector.ps1 -Restart
```

Stop the collector when you no longer need it. The container is started with `--rm`, so `docker stop` also removes it; use `-Force` to clean up a container that exited abnormally:

```powershell
.\scripts\Stop-CopilotTelemetryCollector.ps1
.\scripts\Stop-CopilotTelemetryCollector.ps1 -Force
```

You can also pass `-ConnectionString` explicitly or set `APPLICATIONINSIGHTS_CONNECTION_STRING` if you do not want the script to call Azure CLI. Use `otel-collector-config.sample.yaml` instead only when running the collector directly on your workstation without Docker; that file binds receivers to `127.0.0.1` by default.

If `docker info` cannot connect to `dockerDesktopLinuxEngine`, start Docker Desktop and retry after the daemon is ready.

For a team setup, run the collector on a shared host instead of `localhost`, secure the endpoint, and point VS Code clients at that HTTPS endpoint. Exposing the collector on all host interfaces is an explicit opt-in: use firewall rules, TLS or a private network, and avoid publishing unauthenticated OTLP ports directly to untrusted networks.

## Configure VS Code GitHub Copilot telemetry

Add this to your VS Code user or workspace `settings.json`:

```json
{
  "github.copilot.chat.otel.enabled": true,
  "github.copilot.chat.otel.exporterType": "otlp-http",
  "github.copilot.chat.otel.otlpEndpoint": "http://localhost:4318",
  "github.copilot.chat.otel.captureContent": false
}
```

Set `captureContent` to `true` only if your organization explicitly allows prompt and response content to be collected. Keep it `false` for a safer default.

Restart VS Code after changing these settings, then run a few Copilot Chat requests.

## Verify ingestion

Open Application Insights Logs and run:

```kusto
dependencies
| where timestamp > ago(1h)
| where cloud_RoleName == "copilot-chat"
| take 50
```

If this returns rows, telemetry is reaching Application Insights. If it does not, check the collector logs:

```powershell
docker logs otel-collector
```

Common causes are an incorrect Application Insights connection string, a blocked port `4318`, or an endpoint mismatch between VS Code and the collector.

You can also verify from Azure CLI. The first run may prompt to install the `application-insights` extension.

```powershell
az monitor app-insights query `
  --app "$namePrefixNormalized-appi" `
  --resource-group $resourceGroupName `
  --analytics-query "dependencies | where timestamp > ago(24h) | summarize Count=count(), First=min(timestamp), Last=max(timestamp) by cloud_RoleName | order by Count desc" `
  --output table

az monitor app-insights query `
  --app "$namePrefixNormalized-appi" `
  --resource-group $resourceGroupName `
  --analytics-query "customMetrics | where timestamp > ago(24h) | summarize Count=count() by name | order by Count desc | take 20" `
  --output table
```

For workspace-based Application Insights, the same metrics appear in the Log Analytics workspace as `AppMetrics`. The first run may prompt to install the `log-analytics` extension.

```powershell
$workspaceId = az monitor log-analytics workspace show `
  --resource-group $resourceGroupName `
  --workspace-name "$namePrefixNormalized-law" `
  --query customerId `
  --output tsv

az monitor log-analytics query `
  --workspace $workspaceId `
  --analytics-query "AppMetrics | where TimeGenerated > ago(24h) | summarize Count=count() by Name | order by Count desc | take 20" `
  --output table
```

## Save or import the Grafana dashboard

Open the Azure dashboard gallery for the GitHub Copilot dashboard:

```text
https://aka.ms/amg/dash/gh-copilot
```

This link opens the Azure portal dashboard gallery, not a raw Grafana JSON file. The first view is a template preview. To keep and reuse the dashboard, select `Save As`, then save a copy to the subscription and resource group. From the saved copy, use `Copy to Managed Grafana` if you want it inside the Azure Managed Grafana instance created by this template.

Choose the `Azure Monitor` data source and the Application Insights resource created by this template. Grafana should be able to query the Application Insights data because its managed identity has `Monitoring Reader` on the Log Analytics workspace and Application Insights resource.

If the dashboard shows `No data` with panel errors such as `Invalid application identity provided`, wait a few minutes for Azure RBAC changes to propagate, then reload the dashboard. This can happen shortly after assigning `Grafana Admin` to your user or `Monitoring Reader` to the Managed Grafana identity. During the validated setup, data appeared after reloading with the following filters:

- `Data Source`: `Azure Monitor`
- `Subscription`: the subscription used for deployment
- `Resource Group`: the resource group used for deployment, for example `rg-copilot-monitoring`
- `Application Insights`: the Application Insights resource created from the prefix, for example `copilotmon-appi`
- `Source`: `VS Code Copilot`

Use `Copy to Managed Grafana` from the saved dashboard if you want the dashboard to appear inside the Managed Grafana instance. The Azure Monitor data source in the Managed Grafana instance should be named `Azure Monitor` and use managed identity authentication.

## Cleanup

Deleting the resource group removes the Azure resources:

```powershell
az group delete --name $resourceGroupName
```

Stop the local collector when you no longer need it:

```powershell
docker stop otel-collector
```
