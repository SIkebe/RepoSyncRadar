# GitHub Copilot monitoring with Azure Managed Grafana

This folder creates the Azure side of the monitoring pipeline described in the Microsoft Learn article "Monitor AI coding agents with Grafana" for GitHub Copilot.

## What gets deployed

- Log Analytics workspace
- Workspace-based Application Insights resource
- Azure Managed Grafana 11, Standard SKU
- A `Monitoring Reader` role assignment for Grafana's managed identity at the resource group scope

The OpenTelemetry Collector and VS Code GitHub Copilot telemetry settings are intentionally not deployed by Bicep because they run on your developer machine or on whichever shared host receives OTLP traffic.

## Prerequisites

1. Azure subscription where you can create resource groups and role assignments.
2. Azure CLI with Bicep support: `az version` should show a recent CLI and Bicep installed.
3. Docker Desktop or another Docker runtime for the local OpenTelemetry Collector.
4. VS Code with GitHub Copilot Chat.

## Deploy

Pick a short, globally distinctive prefix and edit `main.bicepparam`. The default prefix is only a placeholder.

```powershell
az login
az account set --subscription "<subscription-id-or-name>"
az provider register --namespace Microsoft.Dashboard --wait
az provider show --namespace Microsoft.Dashboard --query registrationState --output tsv
az group create --name rg-copilot-monitoring --location japaneast
az deployment group what-if `
  --resource-group rg-copilot-monitoring `
  --template-file infra/copilot-grafana/main.bicep `
  --parameters infra/copilot-grafana/main.bicepparam
az deployment group create `
  --resource-group rg-copilot-monitoring `
  --template-file infra/copilot-grafana/main.bicep `
  --parameters infra/copilot-grafana/main.bicepparam
```

The deployment output includes the Grafana endpoint and the Application Insights connection string. Treat the connection string as a secret because anyone with it can send telemetry into your Application Insights resource.

If you cannot open or administer the Grafana instance after deployment, assign yourself the Azure `Grafana Admin` role on the Managed Grafana resource. Azure Managed Grafana uses Azure RBAC for user access.

```powershell
$assignee = az ad signed-in-user show --query id --output tsv
$grafanaId = az resource show `
  --resource-group rg-copilot-monitoring `
  --resource-type Microsoft.Dashboard/grafana `
  --name copilotmon-grafana `
  --query id `
  --output tsv
az role assignment create --assignee $assignee --role "Grafana Admin" --scope $grafanaId
```

## Run the OpenTelemetry Collector locally

Copy `otel-collector-config.sample.yaml` to a local ignored file such as `otel-collector-config.yaml`, then replace the connection string with the deployment output.

```powershell
$connectionString = az resource show `
  --resource-group rg-copilot-monitoring `
  --resource-type Microsoft.Insights/components `
  --name copilotmon-appi `
  --query properties.ConnectionString `
  --output tsv

New-Item -ItemType Directory -Force artifacts/copilot-grafana | Out-Null
Copy-Item infra/copilot-grafana/otel-collector-config.sample.yaml artifacts/copilot-grafana/otel-collector-config.yaml
(Get-Content artifacts/copilot-grafana/otel-collector-config.yaml) `
  -replace 'InstrumentationKey=<YOUR-KEY>;IngestionEndpoint=https://<region>.in.applicationinsights.azure.com/;LiveEndpoint=https://<region>.livediagnostics.monitor.azure.com/;ApplicationId=<YOUR-APP-ID>', $connectionString `
  | Set-Content artifacts/copilot-grafana/otel-collector-config.yaml -Encoding utf8NoBOM

docker info --format "Docker Server {{.ServerVersion}}"
docker run --rm -d --name otel-collector `
  -p 4318:4318 `
  -p 4317:4317 `
  -v ${PWD}/artifacts/copilot-grafana/otel-collector-config.yaml:/etc/otelcol-contrib/config.yaml `
  otel/opentelemetry-collector-contrib:latest

docker ps --filter name=otel-collector --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
```

If `docker info` cannot connect to `dockerDesktopLinuxEngine`, start Docker Desktop and retry after the daemon is ready.

For a team setup, run the collector on a shared host instead of `localhost`, secure the endpoint, and point VS Code clients at that HTTPS endpoint.

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
  --app copilotmon-appi `
  --resource-group rg-copilot-monitoring `
  --analytics-query "dependencies | where timestamp > ago(24h) | summarize Count=count(), First=min(timestamp), Last=max(timestamp) by cloud_RoleName | order by Count desc" `
  --output table

az monitor app-insights query `
  --app copilotmon-appi `
  --resource-group rg-copilot-monitoring `
  --analytics-query "customMetrics | where timestamp > ago(24h) | summarize Count=count() by name | order by Count desc | take 20" `
  --output table
```

For workspace-based Application Insights, the same metrics appear in the Log Analytics workspace as `AppMetrics`. The first run may prompt to install the `log-analytics` extension.

```powershell
$workspaceId = az monitor log-analytics workspace show `
  --resource-group rg-copilot-monitoring `
  --workspace-name copilotmon-law `
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

Choose the `Azure Monitor` data source and the Application Insights resource created by this template. Grafana should be able to query the Application Insights data because its managed identity has `Monitoring Reader` on the resource group.

If the dashboard shows `No data` with panel errors such as `Invalid application identity provided`, wait a few minutes for Azure RBAC changes to propagate, then reload the dashboard. This can happen shortly after assigning `Grafana Admin` to your user or `Monitoring Reader` to the Managed Grafana identity. During the validated setup, data appeared after reloading with the following filters:

- `Data Source`: `Azure Monitor`
- `Subscription`: the subscription used for deployment
- `Resource Group`: `rg-copilot-monitoring`
- `Application Insights`: `copilotmon-appi`
- `Source`: `VS Code Copilot`

Use `Copy to Managed Grafana` from the saved dashboard if you want the dashboard to appear inside the Managed Grafana instance. The Azure Monitor data source in the Managed Grafana instance should be named `Azure Monitor` and use managed identity authentication.

## Cleanup

Deleting the resource group removes the Azure resources:

```powershell
az group delete --name rg-copilot-monitoring
```

Stop the local collector when you no longer need it:

```powershell
docker stop otel-collector
```
