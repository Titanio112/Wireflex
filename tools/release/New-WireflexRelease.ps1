<#
.SYNOPSIS
  Gera o pacote portatil do Wireflex: Wireflex-<versao>-win-x64.zip e o arquivo .sha256.

.DESCRIPTION
  Publica o app self-contained em um unico executavel (win-x64), monta o zip com
  Wireflex.App.exe e LEIA-ME.txt, confere o conteudo e calcula o SHA-256.
  Falha se o zip tiver qualquer outro arquivo ou se o executavel contiver o nome
  de usuario do Windows (caminhos de build).

.EXAMPLE
  & .\tools\release\New-WireflexRelease.ps1 -Version 2.0.0-beta.1
#>
[CmdletBinding()]
param(
    [string]$Version = '2.0.0-beta.1',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\release' }
$publishDir = Join-Path $root 'artifacts\publish\win-x64'
$project = Join-Path $root 'src\Wireflex.App\Wireflex.App.csproj'
$readme = Join-Path $PSScriptRoot 'LEIA-ME.txt'
$zipName = "Wireflex-$Version-win-x64.zip"
$zipPath = Join-Path $OutputDirectory $zipName
$expected = @('LEIA-ME.txt', 'Wireflex.App.exe')

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

# Um exe so, com o runtime e as bibliotecas nativas do WPF dentro.
# DebugType=none: sem PDB e sem o caminho do PDB (que tem a pasta do usuario) no exe.
& dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -p:Deterministic=true -p:ContinuousIntegrationBuild=true `
    -p:Version=$Version -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou ($LASTEXITCODE)" }

$exe = Join-Path $publishDir 'Wireflex.App.exe'
$published = @(Get-ChildItem $publishDir -File | ForEach-Object { $_.Name })
if ($published.Count -ne 1 -or $published[0] -ne 'Wireflex.App.exe') {
    throw "A publicacao deveria ter so o Wireflex.App.exe; tem: $($published -join ', ')"
}

# Nada de caminho pessoal no executavel, em ASCII ou UTF-16 (o bundle nao e comprimido: da para procurar nos bytes).
# Latin-1 mapeia cada byte para um caractere, entao a busca no texto equivale a busca nos bytes.
$raw = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($exe))
foreach ($name in @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ }) {
    $utf16 = ($name.ToCharArray() | ForEach-Object { "$_`0" }) -join ''
    foreach ($needle in @($name, $utf16)) {
        if ($raw.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw 'O executavel contem um nome pessoal (caminho de build?). Nao publique.'
        }
    }
}
Remove-Variable raw

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $exe, 'Wireflex.App.exe', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $readme, 'LEIA-ME.txt', [IO.Compression.CompressionLevel]::Optimal) | Out-Null
}
finally {
    $zip.Dispose()
}

# Confere o zip: exatamente os dois arquivos, nada de .git, arquivos de IA, config ou log.
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($zip.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    $readmeText = (New-Object IO.StreamReader($zip.GetEntry('LEIA-ME.txt').Open())).ReadToEnd()
}
finally {
    $zip.Dispose()
}

if (($entries -join '|') -ne (($expected | Sort-Object) -join '|')) {
    throw "Conteudo inesperado no zip: $($entries -join ', ')"
}
$forbidden = '(^|/)\.git|AGENTS\.md|CLAUDE|MEMORY|\.claude|memory|config\.json|\.log$|\.pdb$'
if ($entries | Where-Object { $_ -match $forbidden }) { throw "Arquivo proibido no zip: $($entries -join ', ')" }
foreach ($name in @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ }) {
    if ($readmeText.IndexOf($name, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'O LEIA-ME contem um nome pessoal.' }
}

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = "$zipPath.sha256"
[IO.File]::WriteAllText($shaPath, "$hash  $zipName`r`n", (New-Object Text.UTF8Encoding $false))

Write-Host "Zip:     $zipPath"
Write-Host "SHA-256: $hash"
Write-Host "Arquivo: $shaPath"
Write-Host "Conteudo: $($entries -join ', ')"
