$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project tests/S9Battery.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    dotnet publish src/S9Battery.Tray -c Release -r win-x64 --self-contained true -o dist/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    Copy-Item README.md dist/win-x64/README.md
    New-Item -ItemType Directory -Path dist/win-x64/docs -Force | Out-Null
    Copy-Item docs/PROTOCOL.md dist/win-x64/docs/PROTOCOL.md
    Compress-Archive -Path dist/win-x64/S9Battery.exe, dist/win-x64/README.md, dist/win-x64/docs -DestinationPath dist/S9Battery-win-x64.zip -Force
    Write-Host 'Program: dist/win-x64/S9Battery.exe'
} finally { Pop-Location }
