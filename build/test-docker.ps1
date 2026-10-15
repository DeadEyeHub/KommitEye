param([string]$Image = 'kommiteye-server:local')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $repo ('artifacts/docker-check-' + [guid]::NewGuid().ToString('N'))
$project = 'kommiteye-check-' + [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Copy-Item (Join-Path $repo 'deploy/docker/compose.yaml'), (Join-Path $repo 'deploy/docker/server.json') $fixture
# A unique Compose project and ephemeral loopback port never disturb a user's deployment.
$composeFile = Join-Path $fixture 'compose.yaml'
$content = [IO.File]::ReadAllText($composeFile).Replace('kommiteye-server:local', $Image).Replace('127.0.0.1:${KOMMITEYE_PORT:-5080}:5080', '127.0.0.1::5080')
[IO.File]::WriteAllText($composeFile, $content)
function Compose {
    $result = & docker compose -p $project -f $composeFile @args
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed: $($args[0])" }
    return $result
}
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message }; Write-Host "PASS: $Message" }
function Request([string]$Path, $Body = $null) {
    $parameters = @{ Uri = "$script:base/api/v1/$Path"; WebSession = $script:session; Headers = @{'X-KommitEye-Client'='web'} }
    if ($null -ne $Body) { $parameters.Method = 'Post'; $parameters.Body = $Body | ConvertTo-Json -Depth 20; $parameters.ContentType = 'application/json' }
    Invoke-RestMethod @parameters
}
try {
    Compose run --rm init | Out-Host
    $firstHash = Compose run --rm --no-deps --entrypoint sha256sum server /run/secrets/key_certificate
    Compose run --rm init | Out-Host
    $secondHash = Compose run --rm --no-deps --entrypoint sha256sum server /run/secrets/key_certificate
    Check ($firstHash -eq $secondHash) 'initialization preserves the original certificate'
    Compose up -d --no-build --wait --wait-timeout 120 server | Out-Host
    $script:base = 'http://' + (Compose port server 5080).Trim()
    $script:session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $password = (Compose exec -T server cat /run/secrets/admin_password).Trim()
    $setup = Request 'setup'
    Check (!$setup.required) 'administrator initialized from secret file without remote setup bypass'
    Request 'login' @{password=$password} | Out-Null
    $info = Request 'system/info'
    Check ($info.dataDirectory -eq '/data') 'server uses persistent /data'
    Check ((Invoke-WebRequest "$script:base/").Content.Contains('<div id="root">')) 'container serves bundled React UI'
    Check ((Compose exec -T server id -u).Trim() -eq '10001') 'server runs as a non-root user'
    Compose exec -T server sh -c 'git init -b main /repos/fixture >/dev/null && git -C /repos/fixture -c user.name=Fixture -c user.email=fixture@example.invalid commit --allow-empty -m fixture >/dev/null' | Out-Null
    $branches = Request 'repositories/branches' @{path='/repos/fixture'}
    Check (($branches | ConvertTo-Json -Depth 10).Contains('main')) 'server reads branches through Linux Git'
    $smtp = Request 'mail/smtp'
    $smtp.settings.host = 'smtp.example.invalid'; $smtp.settings.from = 'sender@example.invalid'; $smtp.settings.recipients = 'recipient@example.invalid'
    Request 'mail/smtp' @{revision=$smtp.revision;settings=$smtp.settings;password='docker-fixture-secret';clearPassword=$false;activate=$true} | Out-Null
    Check ((Request 'mail/smtp').passwordConfigured) 'SMTP secret is saved'
    Compose up -d --no-build --force-recreate --wait --wait-timeout 120 server | Out-Host
    $script:base = 'http://' + (Compose port server 5080).Trim()
    Check ((Request 'session').user -eq 'admin') 'session cookie and certificate-protected key ring survive container replacement'
    Check ((Request 'mail/smtp').passwordConfigured) 'encrypted mail configuration survives container replacement'
    Request 'logout' @{} | Out-Null
    Request 'login' @{password=$password} | Out-Null
    Check ((Request 'system/info').version -eq $info.version) 'administrator password and application version survive replacement'
    Compose stop -t 30 server | Out-Host
    $container = (Compose ps -a -q server).Trim()
    $exitCode = & docker inspect --format '{{.State.ExitCode}}' $container
    Check ($LASTEXITCODE -eq 0 -and $exitCode -eq '0') 'SIGTERM stops the server cleanly'
    $failure = & docker compose -p $project -f $composeFile run --rm --no-deps -e KOMMITEYE_KEY_CERTIFICATE=/missing.pfx server 2>&1
    Check ($LASTEXITCODE -ne 0) 'missing protection certificate prevents startup instead of saving plaintext'
    Compose run --rm --entrypoint sh init -c 'mv /bootstrap/secrets/server.pfx /bootstrap/secrets/server.pfx.backup' | Out-Null
    try {
        $failure = & docker compose -p $project -f $composeFile run --rm init 2>&1
        Check ($LASTEXITCODE -ne 0) 'initializer refuses to replace a lost certificate for existing data'
    } finally {
        Compose run --rm --entrypoint sh init -c 'mv /bootstrap/secrets/server.pfx.backup /bootstrap/secrets/server.pfx' | Out-Null
    }
    Compose run --rm init | Out-Host
    $restoredHash = Compose run --rm --no-deps --entrypoint sha256sum server /run/secrets/key_certificate
    Check ($restoredHash -eq $firstHash) 'reinitialization with existing data preserves the protection certificate'

} catch {
    Compose logs --tail 80 server | Out-Host
    throw
} finally {
    Compose down --remove-orphans | Out-Host
    # Delete only this test's generated files through their isolated container mount.
    & docker run --rm --user 0:0 --entrypoint sh --mount "type=bind,source=$fixture,target=/fixture" $Image -c 'rm -rf /fixture/local'
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not clean fixture: $fixture" }
}
