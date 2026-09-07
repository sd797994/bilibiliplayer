[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'BiliBiliPlayer\BiliBiliPlayer.csproj'
dotnet build $projectPath -c Debug -f net9.0-windows10.0.19041.0 -r win-x64 -p:TargetFrameworks=net9.0-windows10.0.19041.0 -m:1 -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Debug build failed: $LASTEXITCODE" }

$outputDirectory = Join-Path $repositoryRoot 'artifacts'
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$logPath = Join-Path $outputDirectory ('native-playback-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.jsonl')
$executablePath = Join-Path $repositoryRoot 'BiliBiliPlayer\bin\Debug\net9.0-windows10.0.19041.0\win-x64\BiliBiliPlayer.exe'
$previousEnabled = $env:BILI_PLAYBACK_SMOKE
$previousRepro = $env:BILI_PLAYBACK_SMOKE_REPRO
$previousLog = $env:BILI_PLAYBACK_SMOKE_LOG
try {
    $env:BILI_PLAYBACK_SMOKE = '1'
    $env:BILI_PLAYBACK_SMOKE_REPRO = '0'
    $env:BILI_PLAYBACK_SMOKE_LOG = $logPath
    $process = Start-Process -FilePath $executablePath -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(480000)) {
        # Only stop the smoke-test process launched above, never an existing user instance.
        $process.Kill()
        throw "Native playback smoke timed out. Log: $logPath"
    }
    $process.WaitForExit()
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath }
    if ($process.ExitCode -ne 0) { throw "Native playback smoke failed. Log: $logPath" }
    $lastResult = Get-Content -LiteralPath $logPath -Tail 1 | ConvertFrom-Json
    if ($lastResult.value.result -ne 'PASS') { throw "Smoke test did not report PASS. Log: $logPath" }
    Write-Host "Native playback smoke passed. Log: $logPath"
}
finally {
    $env:BILI_PLAYBACK_SMOKE = $previousEnabled
    $env:BILI_PLAYBACK_SMOKE_REPRO = $previousRepro
    $env:BILI_PLAYBACK_SMOKE_LOG = $previousLog
}
