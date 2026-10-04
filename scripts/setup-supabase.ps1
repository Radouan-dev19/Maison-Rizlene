$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$settingsPath = Join-Path $projectRoot '.env.local'
if (-not (Test-Path -LiteralPath $settingsPath)) { throw 'Créez .env.local à partir de .env.example avant de lancer ce script.' }

$settings = @{}
foreach ($line in Get-Content -LiteralPath $settingsPath) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }
    $parts = $trimmed.Split('=', 2)
    if ($parts.Count -ne 2) { throw 'Ligne invalide dans .env.local.' }
    $settings[$parts[0].Trim()] = $parts[1].Trim()
}
foreach ($name in 'SUPABASE_URL', 'SUPABASE_ACCESS_TOKEN', 'ADMIN_EMAIL', 'SITE_URL') {
    if ([string]::IsNullOrWhiteSpace($settings[$name])) { throw "Valeur $name manquante dans .env.local." }
}
$url = $settings['SUPABASE_URL'].TrimEnd('/')
if ($url -notmatch '^https://([a-z0-9-]+)\.supabase\.co$') { throw 'SUPABASE_URL invalide.' }
$projectRef = $Matches[1]
$email = $settings['ADMIN_EMAIL']
$siteUrl = $settings['SITE_URL']
if ($siteUrl -notmatch '^https?://') { throw 'SITE_URL invalide.' }
if ($email -notmatch '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$') { throw 'ADMIN_EMAIL invalide.' }
if ($email -eq 'vous@exemple.fr') { throw 'Remplacez l’adresse e-mail exemple par la vôtre.' }
if ($settings['SUPABASE_ACCESS_TOKEN'] -notmatch '^sbp_[A-Za-z0-9_-]{20,}$') { throw 'Jeton personnel Supabase manquant ou invalide.' }

$managementUrl = "https://api.supabase.com/v1/projects/$projectRef/database/query"
$managementHeaders = @{ Authorization = "Bearer $($settings['SUPABASE_ACCESS_TOKEN'])" }

Write-Output 'Récupération des clés du projet...'
$apiKeys = @(Invoke-RestMethod -Uri "https://api.supabase.com/v1/projects/$projectRef/api-keys?reveal=true" -Headers $managementHeaders)
$publishable = $apiKeys | Where-Object { $_.type -eq 'publishable' -and $_.api_key -like 'sb_publishable_*' } | Select-Object -First 1
$secret = $apiKeys | Where-Object { $_.type -eq 'secret' -and $_.api_key -like 'sb_secret_*' } | Select-Object -First 1
if (-not $publishable -or -not $secret) { throw 'Clés publishable/secret introuvables. Vérifiez les permissions API Keys Read et API Key Secrets Read du jeton.' }
$settings['SUPABASE_PUBLISHABLE_KEY'] = $publishable.api_key
$settings['SUPABASE_SECRET_KEY'] = $secret.api_key
function Invoke-Sql([string]$query) {
    $body = @{ query = $query; read_only = $false } | ConvertTo-Json -Compress
    return Invoke-RestMethod -Uri $managementUrl -Method Post -Headers $managementHeaders -ContentType 'application/json' -Body $body
}

Write-Output 'Installation du schéma Supabase...'
$schema = Get-Content -LiteralPath (Join-Path $projectRoot 'supabase\schema.sql') -Raw
Invoke-Sql $schema | Out-Null

$rows = @(Invoke-Sql "select id::text as id from auth.users where email = '$email' limit 1")
if ($rows.Count -eq 0 -or [string]::IsNullOrWhiteSpace($rows[0].id)) {
    Write-Output 'Envoi de l’invitation administrateur...'
    $inviteBody = @{ email = $email } | ConvertTo-Json -Compress
    $inviteHeaders = @{ apikey = $settings['SUPABASE_SECRET_KEY'] }
    $redirect = [uri]::EscapeDataString($siteUrl)
    Invoke-RestMethod -Uri "$url/auth/v1/invite?redirect_to=$redirect" -Method Post -Headers $inviteHeaders -ContentType 'application/json' -Body $inviteBody | Out-Null
}

$adminRows = @(Invoke-Sql "insert into public.admins (id) select id from auth.users where email = '$email' on conflict (id) do update set id = excluded.id returning id::text as id")
if ($adminRows.Count -eq 0 -or [string]::IsNullOrWhiteSpace($adminRows[0].id)) { throw 'Compte Auth introuvable après invitation. Vérifiez l’adresse et relancez.' }
Write-Output 'Schéma installé et rôle administrateur attribué. Ouvrez l’e-mail d’invitation pour définir votre mot de passe.'
$runtimeSettings = @(
    "SUPABASE_URL=$url",
    "SUPABASE_PUBLISHABLE_KEY=$($settings['SUPABASE_PUBLISHABLE_KEY'])",
    "SUPABASE_SECRET_KEY=$($settings['SUPABASE_SECRET_KEY'])",
    "ADMIN_EMAIL=$email",
    "SITE_URL=$siteUrl"
)
[System.IO.File]::WriteAllLines($settingsPath, $runtimeSettings, [System.Text.UTF8Encoding]::new($false))
Write-Output 'Les clés serveur sont enregistrées dans .env.local (ignoré par Git) ; le jeton personnel a été retiré.'
