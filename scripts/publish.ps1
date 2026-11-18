[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.\-][0-9A-Za-z]+)*)?$')]
    [string] $Version,
    [string] $Source
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src/1337res/1337res.csproj'
$publishDirectory = Join-Path $repository 'artifacts/publish/win-x64'
$releaseDirectory = Join-Path $repository 'artifacts/win-x64'
$releaseName = '1337res-win-x64.exe'

$publishArguments = @(
    'publish', $project,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $publishDirectory,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
if ($Version) {
    $publishArguments += "-p:Version=$Version"
}
if ($Source) {
    $publishArguments += '--source', $Source
}

& $Dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

New-Item -Path $releaseDirectory -ItemType Directory -Force | Out-Null
$executable = Join-Path $releaseDirectory $releaseName
Copy-Item -LiteralPath (Join-Path $publishDirectory '1337res.exe') -Destination $executable -Force
$sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText("$executable.sha256", "$sha256  $releaseName`n", [System.Text.Encoding]::ASCII)
Write-Output "Portable executable: $executable"
Write-Output "SHA-256 checksum: $executable.sha256"
