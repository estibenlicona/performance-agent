<#
.SYNOPSIS
  Comprueba que la configuración versionada del workspace es coherente y segura.

.DESCRIPTION
  - Copilot solo ve el servidor performance-agent, y .mcp.json apunta a la versión fijada en setup/tools.json.
  - El agente solo tiene performance-agent/* y las herramientas de lectura; la skill y el prompt son válidos.
  - safety-policy.json mantiene los valores restrictivos por defecto.
  - Si el servidor está instalado, las versiones de los proveedores que espera coinciden con setup/tools.json.
  Lo ejecuta setup/bootstrap.ps1 al terminar; también se puede lanzar solo tras editar la configuración.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Read-Json([string]$relativePath) {
    # Admite comentarios de línea completa (//), como safety-policy.json.
    $lines = Get-Content -Path (Join-Path $root $relativePath) | Where-Object { $_ -notmatch '^\s*//' }
    return ($lines -join "`n") | ConvertFrom-Json
}

function Read-Frontmatter([string]$relativePath) {
    $text = (Get-Content -Raw -Path (Join-Path $root $relativePath)) -replace "`r`n", "`n"
    if ($text -notmatch '(?s)^---\n(?<yaml>.*?)\n---\n(?<body>.*)$') {
        $failures.Add("${relativePath}: falta el frontmatter YAML.")
        return $null
    }

    $yaml = $Matches['yaml']
    $body = $Matches['body']
    $fields = @{}
    $listKey = $null
    foreach ($line in $yaml -split "`n") {
        if ($line.StartsWith('  - ') -and $listKey) {
            $fields[$listKey] = @($fields[$listKey]) + $line.Substring(4).Trim() | Where-Object { $_ }
        }
        elseif ($line -match '^(?<key>[^:]+):(?<value>.*)$') {
            $listKey = $Matches['key'].Trim()
            $fields[$listKey] = $Matches['value'].Trim()
        }
    }

    return @{ Fields = $fields; Body = $body }
}

function Check([bool]$condition, [string]$message) {
    if (-not $condition) { $failures.Add($message) }
}

$tools = Read-Json 'setup/tools.json'

# Servidor MCP del workspace
$mcp = Read-Json '.mcp.json'
$servers = @($mcp.mcpServers.PSObject.Properties.Name)
Check ($servers.Count -eq 1 -and $servers[0] -eq 'performance-agent') ".mcp.json debe declarar solo el servidor performance-agent (declara: $($servers -join ', '))."
Check (-not (Test-Path (Join-Path $root '.vscode/mcp.json'))) 'No dupliques servidores en .vscode/mcp.json.'
$expectedCommand = ".tools/performance-agent/$($tools.performanceAgent.version)/$($tools.performanceAgent.executable)"
Check ($mcp.mcpServers.'performance-agent'.command -eq $expectedCommand) ".mcp.json debe ejecutar $expectedCommand (versión fijada en setup/tools.json)."

# Agente, skill y prompt de Copilot
$agent = Read-Frontmatter '.github/agents/performance-diagnostics.agent.md'
if ($agent) {
    Check ((@($agent.Fields['tools']) -join ',') -eq 'performance-agent/*,read,search') 'El agente solo puede tener las herramientas performance-agent/*, read y search.'
    Check ($agent.Fields['target'] -eq 'vscode') 'El agente debe declarar target: vscode.'
    Check (-not [string]::IsNullOrWhiteSpace($agent.Fields['description'])) 'El agente necesita description.'
    Check ($agent.Body.Contains('dotnet-performance-diagnosis')) 'El agente debe referirse a la skill dotnet-performance-diagnosis.'
    Check ($agent.Body.Length -lt 30000) 'GitHub limita el cuerpo del agente a 30.000 caracteres.'
}

$skill = Read-Frontmatter '.github/skills/dotnet-performance-diagnosis/SKILL.md'
if ($skill) {
    Check ($skill.Fields['name'] -eq 'dotnet-performance-diagnosis') 'La skill debe llamarse dotnet-performance-diagnosis (igual que su carpeta).'
    $description = [string]$skill.Fields['description']
    Check ($description.Length -ge 1 -and $description.Length -le 1024) 'La descripción de la skill debe tener entre 1 y 1024 caracteres.'
}

$prompt = Read-Frontmatter '.github/prompts/diagnose-api-performance.prompt.md'
if ($prompt -and $agent) {
    Check ($prompt.Fields['agent'] -eq $agent.Fields['name']) 'El prompt debe apuntar al agente por su nombre.'
    Check (-not $prompt.Fields.ContainsKey('mode')) "'mode' está obsoleto en los prompts: usa 'agent'."
}

# Política restrictiva por defecto
$policy = Read-Json 'safety-policy.json'
Check ((@($policy.allowedLoadTargetHosts) -join ',') -eq '127.0.0.1') 'safety-policy.json: la carga solo puede ir a 127.0.0.1.'
Check (@($policy.allowedRemoteDependencyHosts).Count -eq 0) 'safety-policy.json: allowedRemoteDependencyHosts debe estar vacío por defecto.'
Check ($policy.writeOperations -eq 'deny') 'safety-policy.json: writeOperations debe ser deny.'
Check (-not $policy.diagnostics.allowDumps -and -not $policy.diagnostics.allowHeapInspection -and -not $policy.diagnostics.allowMethodParameterCapture) 'safety-policy.json: dumps, inspección del heap y captura de parámetros deben estar desactivados.'
Check ($policy.load.maxDurationSeconds -le 270) 'safety-policy.json: maxDurationSeconds no puede superar 270 (mcp-k6 corta run_script a los 5 minutos).'

# Versiones de los proveedores que espera el servidor instalado
$serverSettings = Join-Path $root ".tools/performance-agent/$($tools.performanceAgent.version)/appsettings.json"
if (Test-Path $serverSettings) {
    $providers = (Get-Content -Raw $serverSettings | ConvertFrom-Json).CommunityProviders
    Check ($providers.K6.ExpectedVersion -eq $tools.k6.version) "El servidor espera k6 $($providers.K6.ExpectedVersion); setup/tools.json fija $($tools.k6.version)."
    Check ($providers.K6Mcp.ExpectedVersion -eq $tools.mcpK6.version) "El servidor espera mcp-k6 $($providers.K6Mcp.ExpectedVersion); setup/tools.json fija $($tools.mcpK6.version)."
    Check ($providers.DotNetDiagnosticsMcp.ExpectedVersion -eq $tools.dotnetDiagnosticsMcp.version) "El servidor espera dotnet-diagnostics-mcp $($providers.DotNetDiagnosticsMcp.ExpectedVersion); setup/tools.json fija $($tools.dotnetDiagnosticsMcp.version)."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host "[error] $_" -ForegroundColor Red }
    throw "La configuración del workspace no es válida ($($failures.Count) problemas)."
}

Write-Host '[ok] Configuración del workspace verificada'
