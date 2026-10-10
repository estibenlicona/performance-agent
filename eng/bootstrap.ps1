<#
.SYNOPSIS
  Instala las herramientas externas fijadas en eng/tools.json dentro de .tools/ (no modifica el sistema).

.DESCRIPTION
  - Descarga k6 y grafana/mcp-k6 desde sus releases oficiales de GitHub.
  - Verifica el SHA256 contra el valor fijado en eng/tools.json Y contra el archivo de checksums del release.
  - Extrae cada herramienta en su propio directorio vacío: .tools/<herramienta>/<versión>/.
  - Restaura dotnet-diagnostics-mcp como herramienta LOCAL (.config/dotnet-tools.json).
  Es idempotente: si la versión ya está instalada y verificada, no vuelve a descargarla.
  Probado en Windows 11 x64 con Windows PowerShell 5.1 y PowerShell 7.
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw -Path (Join-Path $PSScriptRoot 'tools.json') | ConvertFrom-Json
$toolsDir = Join-Path $root '.tools'
$downloads = Join-Path $toolsDir 'downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null

if ([Environment]::OSVersion.Platform -ne 'Win32NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'Este script solo instala binarios windows-amd64. En otros sistemas instala k6/mcp-k6 manualmente con las mismas versiones (ver docs/providers.md).'
}

function Install-ReleaseTool([string]$name, $tool) {
    $asset = $tool.assets.'windows-amd64'
    $target = Join-Path $toolsDir (Join-Path $name $tool.version)
    $exe = Join-Path $target $asset.executable
    $marker = Join-Path $target '.verified-sha256'
    if (-not $Force -and (Test-Path $exe) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $asset.sha256)) {
        Write-Host "[ok] $name $($tool.version) ya instalado: $exe"
        return
    }

    $fileName = Split-Path -Leaf $asset.url
    $zip = Join-Path $downloads $fileName
    Write-Host "[..] Descargando $name $($tool.version) desde $($asset.url)"
    Invoke-WebRequest -Uri $asset.url -OutFile $zip -UseBasicParsing

    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($actual -ne $asset.sha256.ToLowerInvariant()) {
        Remove-Item $zip -Force
        throw "SHA256 no coincide para $fileName. Esperado $($asset.sha256), obtenido $actual. Descarga eliminada."
    }

    $checksums = (Invoke-WebRequest -Uri $tool.checksums -UseBasicParsing).Content
    if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::UTF8.GetString($checksums) }
    $line = ($checksums -split "`n") | Where-Object { $_ -match [Regex]::Escape($fileName) } | Select-Object -First 1
    if (-not $line -or -not $line.ToLowerInvariant().StartsWith($actual)) {
        throw "El archivo de checksums del release no confirma $fileName ($actual)."
    }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Expand-Archive -Path $zip -DestinationPath $target -Force
    if (-not (Test-Path $exe)) {
        throw "No se encontró $($asset.executable) tras extraer $fileName."
    }

    Set-Content -Path $marker -Value $asset.sha256 -NoNewline
    Write-Host "[ok] $name $($tool.version) instalado y verificado: $exe"
}

Install-ReleaseTool 'k6' $manifest.k6
Install-ReleaseTool 'mcp-k6' $manifest.mcpK6

Push-Location $root
try {
    Write-Host '[..] Restaurando herramientas .NET locales (dotnet-diagnostics-mcp)'
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore falló.' }
    Write-Host "[ok] dotnet-diagnostics-mcp $($manifest.dotnetDiagnosticsMcp.version) restaurado (dotnet tool run dotnet-diagnostics-mcp)"
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Herramientas listas. Siguiente paso: dotnet build dotnet/PerfAgent.slnx -c Release'
