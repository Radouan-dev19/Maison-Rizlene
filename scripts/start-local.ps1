$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$settingsPath = Join-Path $projectRoot '.env.local'
if (-not (Test-Path -LiteralPath $settingsPath)) { throw 'Créez .env.local à partir de .env.example.' }
foreach ($line in Get-Content -LiteralPath $settingsPath) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
    $parts = $trimmed.Split('=', 2)
    if ($parts.Count -ne 2) { throw 'Ligne invalide dans .env.local.' }
    if ($parts[0].Trim() -in 'SUPABASE_URL', 'SUPABASE_PUBLISHABLE_KEY', 'SUPABASE_SECRET_KEY') {
        [Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim(), 'Process')
    }
}
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project (Join-Path $projectRoot 'Maison Rizlene.Web\Maison Rizlene.Web.csproj') --no-launch-profile --urls http://127.0.0.1:5080
