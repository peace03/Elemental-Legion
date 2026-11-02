param(
    [ValidateSet('normal','m3-demo')][string]$Scenario='normal',
    [switch]$ReportNetwork,
    [switch]$ReportGameplay,
    [switch]$BuildOnly
)
$ErrorActionPreference='Stop'
$clientRoot=Split-Path $PSScriptRoot -Parent
$repositoryRoot=Split-Path $clientRoot -Parent
$dotnetPath=(Get-Command dotnet -ErrorAction Stop).Source
$sdkRoot=Join-Path (Split-Path $dotnetPath -Parent) 'sdk'
$pinned=(Get-Content (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$selected=Join-Path $sdkRoot "$pinned/dotnet.dll"
if(-not(Test-Path -LiteralPath $selected)) {
    $available=Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object {$_.Name -match '^10\.0\.\d+$'} | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
    if(-not $available){throw '.NET 10 SDK가 필요합니다.'}
    $selected=Join-Path $available.FullName 'dotnet.dll'
    Write-Host "고정 SDK $pinned 없음: 설치된 SDK $($available.Name)를 직접 사용합니다. global.json은 유지합니다."
}
$project=Join-Path $repositoryRoot 'server/Server/ElementalLegion.Server.csproj'
$artifacts=Join-Path $clientRoot 'Temp/ServerRun/dotnet-artifacts'
$buildSettings=@('-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$artifacts",'-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
Write-Host '최신 소스를 복원·Release 빌드합니다.'
& $dotnetPath $selected restore $project --locked-mode @buildSettings
if($LASTEXITCODE -ne 0){throw '서버 패키지 복원 실패'}
& $dotnetPath $selected build $project --no-restore -c Release @buildSettings
if($LASTEXITCODE -ne 0){throw '서버 빌드 실패'}
$serverDll=Join-Path $artifacts 'bin/ElementalLegion.Server/release/ElementalLegion.Server.dll'
if($BuildOnly){Write-Host "빌드 완료: $serverDll";exit 0}
Write-Host 'M3 protocol=3 배너를 확인하세요. 이전 서버의 PowerShell에서 Ctrl+C로 종료한 후 접속하세요.'
$serverArgs=@('--listen','--scenario',$Scenario)
if($ReportNetwork){$serverArgs+='--report-network'}
if($ReportGameplay){$serverArgs+='--report-gameplay'}
& $dotnetPath $serverDll @serverArgs
exit $LASTEXITCODE
