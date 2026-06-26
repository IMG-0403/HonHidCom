$ErrorActionPreference = "Stop"

$msbuild = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw ".NET Framework 4 MSBuild was not found."
}

& $msbuild ".\HonHidVerifier.csproj" "/t:Rebuild" "/p:Configuration=Release" "/nologo"
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "Output: .\bin\Release\HoneywellHidTool.exe"
