$ErrorActionPreference = "Stop"

$projectPath = Join-Path $PSScriptRoot "src\MyTodo\MyTodo.csproj"
$publishPath = Join-Path $PSScriptRoot "publish\MyTodo-win-x86"
$zipPath = Join-Path $PSScriptRoot "MyTodo-win-x86-portable.zip"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '未找到 dotnet。请安装 .NET 10 SDK 或 Visual Studio 的“.NET 桌面开发”工作负载。'
}

if (Test-Path $publishPath) {
    Remove-Item $publishPath -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

dotnet publish $projectPath `
    --configuration Release `
    --output $publishPath `
    -p:PlatformTarget=x86 `
    -p:Prefer32Bit=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

Copy-Item (Join-Path $PSScriptRoot "README-PORTABLE.txt") (Join-Path $publishPath "README.txt")
Compress-Archive -Path "$publishPath\*" -DestinationPath $zipPath

Write-Host "构建完成：$zipPath" -ForegroundColor Green
