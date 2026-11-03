param(
    [Parameter(Mandatory)][string]$DotnetPath,
    [Parameter(Mandatory)][string]$ServerDll
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1')
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$work = Join-Path $repositoryRoot '.artifacts/relay-test'
$save = Join-Path $repositoryRoot '.server-data/relay-test'
$addressFile = Join-Path $work 'relay-test-url.txt'
$pendingFile = "$addressFile.pending"
$tunnelLog = Join-Path $work 'cloudflared.log'
$tunnelConfig = Join-Path $work 'cloudflared-test.yml'
$version = '2026.10.7'
$checksum = '2493DBC168923BB3E59E5E7716F71DECED92992EEA9ACD6E9D74F84AC7751343'
$cloudflared = Join-Path $work "cloudflared-$version.exe"

function Start-Child([string]$File, [string]$Arguments, [switch]$CaptureOutput) {
    # 실행기와 같은 콘솔을 사용해 콘솔을 닫을 때 자식도 종료 신호를 받는다.
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $File
    $start.Arguments = $Arguments
    $start.WorkingDirectory = $repositoryRoot
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $CaptureOutput.IsPresent
    return [Diagnostics.Process]::Start($start)
}

New-Item -ItemType Directory -Force -Path $work | Out-Null
try { $sessionLock = [IO.File]::Open((Join-Path $work 'relay.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
catch [IO.IOException] { throw '인터넷 친구 테스트가 이미 실행 중입니다. 먼저 실행 중인 창을 확인하세요.' }
$relay = $null
$tunnel = $null
$download = "$cloudflared.download"
$previousTls = [Net.ServicePointManager]::SecurityProtocol
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Remove-Item -LiteralPath $addressFile, $pendingFile, $tunnelLog -ErrorAction SilentlyContinue
    if (!(Test-Path -LiteralPath $cloudflared -PathType Leaf) -or (Get-FileHash -LiteralPath $cloudflared).Hash -ne $checksum) {
        Write-Host '공식 Cloudflare 터널 도구를 받습니다.'
        $release = "https://github.com/cloudflare/cloudflared/releases/download/$version/cloudflared-windows-amd64.exe"
        Invoke-WebRequest -UseBasicParsing -Uri $release -OutFile $download -TimeoutSec 90
        if ((Get-FileHash -LiteralPath $download).Hash -ne $checksum) { throw 'Cloudflare 도구의 SHA-256 검증에 실패했습니다.' }
        Move-Item -LiteralPath $download -Destination $cloudflared -Force
    }

    Write-Host '로컬 중계 서버를 시작합니다.'
    $relay = Start-Child $DotnetPath ('"{0}" --online --relay-test --save-dir "{1}"' -f $ServerDll, $save) -CaptureOutput
    $wait = [Diagnostics.Stopwatch]::StartNew()
    $origin = $null
    while ($wait.Elapsed.TotalSeconds -lt 15) {
        $line = $relay.StandardOutput.ReadLineAsync()
        while (!$line.Wait(200) -and $wait.Elapsed.TotalSeconds -lt 15) { }
        if (!$line.IsCompleted -or $null -eq $line.Result) { break }
        if ($line.Result -cmatch '^RELAY_ORIGIN=(http://127\.0\.0\.1:[1-9][0-9]{0,4})$') {
            $origin = $Matches[1]
            break
        }
    }
    if (!$origin) { throw '로컬 중계 서버가 준비되지 않았습니다.' }
    $null = $relay.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)

    Write-Host '임시 인터넷 주소를 준비합니다.'
    # 사용자의 기존 Cloudflare 설정 대신 이 테스트의 로컬 중계만 공개한다.
    Set-Content -LiteralPath $tunnelConfig -Value '{}' -Encoding Ascii
    $tunnel = Start-Child $cloudflared ('tunnel --config "{0}" --no-autoupdate --loglevel info --logfile "{1}" --url {2}' -f $tunnelConfig, $tunnelLog, $origin)
    $wait.Restart()
    $url = $null
    $available = $false
    while ($wait.Elapsed.TotalSeconds -lt 60 -and !$tunnel.HasExited -and !$relay.HasExited) {
        if (!$url -and (Test-Path -LiteralPath $tunnelLog -PathType Leaf)) {
            if ((Get-Content -LiteralPath $tunnelLog -Raw -Encoding UTF8) -cmatch 'https://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com(?=\s|["|])') {
                $url = $Matches[0]
            }
        }
        if ($url) {
            try {
                $response = Invoke-WebRequest -UseBasicParsing -Uri "$url/rooms" -MaximumRedirection 0 -TimeoutSec 5
                $available = $response.StatusCode -eq 200 -and ($response.Content | ConvertFrom-Json).rooms -is [array]
            } catch { $available = $false }
            if ($available) { break }
        }
        Start-Sleep -Seconds 1
    }
    if (!$available -or $tunnel.HasExited -or $relay.HasExited) {
        throw "공개 주소에서 방 목록을 확인하지 못했습니다. 터널 로그를 확인하세요. $tunnelLog"
    }

    Set-Content -LiteralPath $pendingFile -Value $url -Encoding Ascii
    Move-Item -LiteralPath $pendingFile -Destination $addressFile
    Write-Host "인터넷 친구 테스트 준비 완료: $url"
    Write-Host '이 창을 켜 둔 채 Unity Play 또는 Windows x64 빌드를 실행하세요. 종료하려면 이 창에서 Enter를 누르세요.'
    Write-Host "기존 테스트 빌드의 주소 갱신 파일: $addressFile"
    while (!$relay.HasExited -and !$tunnel.HasExited) {
        if (![Console]::IsInputRedirected -and [Console]::KeyAvailable -and [Console]::ReadKey($true).Key -eq [ConsoleKey]::Enter) {
            return
        }
        Start-Sleep -Milliseconds 500
    }
    throw "중계 서버 또는 터널이 종료됐습니다. $tunnelLog"
}
finally {
    foreach ($child in @($tunnel, $relay)) {
        if ($null -eq $child) { continue }
        try { if (!$child.HasExited) { $child.Kill(); $null = $child.WaitForExit(3000) } }
        catch [InvalidOperationException] { }
        finally { $child.Dispose() }
    }
    Remove-Item -LiteralPath $addressFile, $pendingFile, $download -ErrorAction SilentlyContinue
    $sessionLock.Dispose()
    [Net.ServicePointManager]::SecurityProtocol = $previousTls
}
