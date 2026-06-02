# Banyan Brain Lite — Local Startup Script
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:BANYAN_EMBEDDER = 'onnx'
$env:BANYAN_EMBEDDER_MODEL = "$env:USERPROFILE\.banyan\embedder\model.onnx"
$env:BANYAN_EMBEDDER_VOCAB = "$env:USERPROFILE\.banyan\embedder\vocab.txt"
$env:BANYAN_EMBEDDER_MODEL_ID = 'bge-small-zh-v1.5.onnx.q8'
$env:BANYAN_EMBEDDER_DIMENSIONS = '384'
$env:BANYAN_EMBEDDER_QUERY_PREFIX = '为这个句子生成表示以用于检索相关文章：'
$env:BANYAN_SQLITE_VEC_LIB = "$env:USERPROFILE\.banyan\sqlite-vec\vec0.dll"
$env:BANYAN_NIP_CA_PASSPHRASE = 'banyan-local-dev'

$project = Join-Path $PSScriptRoot 'src\Banyan.Cli\Banyan.Cli.csproj'

Write-Host "Starting Banyan Brain Lite..." -ForegroundColor Cyan
dotnet run --project $project -- web
