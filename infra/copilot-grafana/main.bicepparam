using './main.bicep'

param location = 'japaneast'
param namePrefix = 'copilotmon'
param retentionInDays = 30
param dailyQuotaGb = 1
param tags = {
  workload: 'github-copilot-monitoring'
  environment: 'dev'
}
