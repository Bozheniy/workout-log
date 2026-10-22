param(
    [string] $BasePath = "/workout-log/",
    [string] $OutputDirectory = "publish"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$indexPath = Join-Path $projectRoot "src/WorkoutLog.Web/wwwroot/index.html"
$originalHtml = [System.IO.File]::ReadAllText($indexPath)

if (-not $BasePath.StartsWith("/") -or -not $BasePath.EndsWith("/")) {
    throw "BasePath должен начинаться и заканчиваться знаком /."
}
if ($BasePath -notmatch '^/[a-zA-Z0-9_/-]*$') {
    throw "Недопустимые символы в BasePath."
}

try {
    # Меняем base ДО publish: хэш index.html в манифесте PWA будет правильным.
    $publishedHtml = $originalHtml.Replace('<base href="/" />', '<base href="' + $BasePath + '" />')
    # Версия страницы позволяет отличить первое включение PWA от обновления.
    $publishedHtml = $publishedHtml.Replace('__APP_BUILD__', [Guid]::NewGuid().ToString('N'))
    [System.IO.File]::WriteAllText($indexPath, $publishedHtml)
    $output = Join-Path $projectRoot $OutputDirectory
    dotnet publish (Join-Path $projectRoot "src/WorkoutLog.Web/WorkoutLog.Web.csproj") `
        --configuration Release --output $output
    if ($LASTEXITCODE -ne 0) {
        throw "Не удалось собрать приложение."
    }
    $webRoot = Join-Path $output "wwwroot"
    Copy-Item -LiteralPath (Join-Path $webRoot "index.html") -Destination (Join-Path $webRoot "404.html")
    [System.IO.File]::WriteAllText((Join-Path $webRoot ".nojekyll"), "")
    Write-Output ("Готово: " + $webRoot)
}
finally {
    [System.IO.File]::WriteAllText($indexPath, $originalHtml)
}
