# One command from a clean clone: builds the UI, checks the contract, runs the tests, publishes Latch
# self-contained and packs the Velopack installer into artifacts/releases.
#   pwsh ./build.ps1            full build + installer
#   pwsh ./build.ps1 -NoPack    skip the installer
param([switch]$NoPack, [string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Step($name, [scriptblock]$body) {
  Write-Host "==> $name" -ForegroundColor Cyan
  & $body
  if ($LASTEXITCODE -ne 0) { throw "$name failed (exit $LASTEXITCODE)" }
}

Step "UI dependencies" { corepack pnpm --dir web install --frozen-lockfile }
Step "UI build" { corepack pnpm --dir web build }
Step "Contract is current" { dotnet run --project tools/Latch.ContractGen -- --check }
Step "Core tests" { dotnet test tests/Latch.Core.Tests -c Release }
Step "Publish (self-contained, ReadyToRun)" {
  dotnet publish src/Latch.Host -c Release -o artifacts/publish -p:Version=$Version
}
if ($NoPack) { return }
Step "Restore vpk" { dotnet tool restore }
Remove-Item artifacts/releases -Recurse -Force -ErrorAction SilentlyContinue  # vpk refuses to repack an existing version
# packId "LatchApp" installs to %LOCALAPPDATA%\LatchApp, apart from user data in %LOCALAPPDATA%\Latch.
Step "Velopack installer" {
  dotnet vpk pack --packId LatchApp --packVersion $Version --packTitle Latch --packAuthors Latch `
    --packDir artifacts/publish --mainExe Latch.exe --icon src/Latch.Host/Latch.ico `
    --framework webview2 --runtime win-x64 --outputDir artifacts/releases
}
Get-ChildItem artifacts/releases -Filter *Setup.exe | ForEach-Object {
  "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB)
}
