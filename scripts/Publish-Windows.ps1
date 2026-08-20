[CmdletBinding()]
param(
    [switch]$SkipShortcut
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'BiliBiliPlayer\BiliBiliPlayer.csproj'
$publishDirectory = Join-Path $repositoryRoot 'artifacts\publish\win-x64'
$executablePath = Join-Path $publishDirectory 'BiliBiliPlayer.exe'

dotnet publish $projectPath `
    -c Release `
    -f net9.0-windows10.0.19041.0 `
    -r win-x64 `
    -p:TargetFrameworks=net9.0-windows10.0.19041.0 `
    --self-contained false `
    -p:WindowsPackageType=None `
    -o $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Published executable was not found: $executablePath"
}

if (-not $SkipShortcut) {
    $desktopDirectory = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($desktopDirectory)) {
        throw 'Windows Desktop directory could not be resolved.'
    }

    $shortcutDisplayName = -join @(
        [char]0x54D4,
        [char]0x54E9,
        [char]0x684C,
        [char]0x9762
    )
    $shortcutPath = Join-Path $desktopDirectory ($shortcutDisplayName + '.lnk')
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $executablePath
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $publishDirectory
    $shortcut.IconLocation = "$executablePath,0"
    $shortcut.Description = 'Launch BiliBiliPlayer'
    $shortcut.Save()

    Write-Host "Desktop shortcut: $shortcutPath"
}

Write-Host "Published application: $executablePath"
