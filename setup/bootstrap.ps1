<#
.SYNOPSIS
  Instala el servidor performance-agent y las herramientas fijadas en setup/tools.json dentro de .tools/
  (no modifica el sistema ni requiere el SDK de .NET) y verifica la configuración del workspace.

.DESCRIPTION
  - Descarga k6, grafana/mcp-k6 y dotnet-diagnostics-mcp (ejecutable autocontenido) desde sus releases oficiales de GitHub.
  - Verifica el SHA256 contra el valor fijado en setup/tools.json Y contra el release (archivo de checksums o digest de GitHub).
  - Si tools.json declara performanceAgent, instala el paquete del servidor (ejecutable autocontenido) y regenera
    scenarios/run-manifest.v1.schema.json con él.
  - Extrae cada herramienta en su propio directorio vacío: .tools/<herramienta>/<versión>/.
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

    if ($tool.checksums) {
        $checksums = (Invoke-WebRequest -Uri $tool.checksums -UseBasicParsing).Content
        if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::UTF8.GetString($checksums) }
        $line = ($checksums -split "`n") | Where-Object { $_ -match [Regex]::Escape($fileName) } | Select-Object -First 1
        if (-not $line -or -not $line.ToLowerInvariant().StartsWith($actual)) {
            throw "El archivo de checksums del release no confirma $fileName ($actual)."
        }
    }
    else {
        # Releases sin archivo de checksums: GitHub publica el SHA256 de cada asset (campo digest).
        $release = Invoke-RestMethod -Uri $tool.releaseApi -UseBasicParsing
        $digest = ($release.assets | Where-Object { $_.name -eq $fileName } | Select-Object -First 1).digest
        if ($digest -ne "sha256:$actual") {
            throw "El digest del release no confirma $fileName ($actual; GitHub: $digest)."
        }
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

function Install-LocalPackage([string]$name, $package) {
    # Paquete local del servidor (hasta que haya releases publicados): se verifica contra el .sha256 que lo acompaña.
    $zip = [IO.Path]::GetFullPath((Join-Path $root $package.package))
    if (-not (Test-Path $zip)) {
        throw "No existe el paquete $zip. Publícalo desde performance-agent-server con setup/publish.ps1."
    }

    $expected = ((Get-Content -Raw "$zip.sha256").Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA256 no coincide para $zip. Esperado $expected, obtenido $actual."
    }

    $target = Join-Path $toolsDir (Join-Path $name $package.version)
    $exe = Join-Path $target $package.executable
    $marker = Join-Path $target '.verified-sha256'
    if (-not $Force -and (Test-Path $exe) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $actual)) {
        Write-Host "[ok] $name $($package.version) ya instalado: $exe"
        return $exe
    }

    if (Test-Path $target) { Remove-Item -Recurse -Force $target }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Expand-Archive -Path $zip -DestinationPath $target -Force
    if (-not (Test-Path $exe)) {
        throw "No se encontró $($package.executable) tras extraer $zip."
    }

    Set-Content -Path $marker -Value $actual -NoNewline
    Write-Host "[ok] $name $($package.version) instalado y verificado: $exe"
    return $exe
}

Install-ReleaseTool 'k6' $manifest.k6
Install-ReleaseTool 'mcp-k6' $manifest.mcpK6
Install-ReleaseTool 'dotnet-diagnostics-mcp' $manifest.dotnetDiagnosticsMcp

if ($manifest.performanceAgent) {
    $server = Install-LocalPackage 'performance-agent' $manifest.performanceAgent
    Push-Location $root
    try {
        & $server schema | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'performance-agent schema falló.' }
        Write-Host '[ok] scenarios/run-manifest.v1.schema.json actualizado desde el servidor instalado'
    }
    finally {
        Pop-Location
    }
}

& (Join-Path $PSScriptRoot 'verify.ps1')

Write-Host ''
Write-Host 'Listo. Abre esta carpeta en VS Code y elige el agente Performance Diagnostics en Copilot Chat.'
