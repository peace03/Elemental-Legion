param(
    [ValidateSet('normal','m3-demo','m4-demo','m5-demo')][string]$Scenario='normal',
    [switch]$Online,
    [switch]$InternetTest,
    [switch]$MigrateWorld,
    [switch]$ReportNetwork,
    [switch]$ReportGameplay,
    [switch]$BuildOnly,
    [switch]$SelectScenario
)
$ErrorActionPreference='Stop'
if($SelectScenario) {
    Write-Host '[1] 일반 서버'
    Write-Host '[2] M3 데모 서버'
    Write-Host '[3] M4 데모 서버'
    Write-Host '[4] M5 데모 서버'
    Write-Host '[5] 인터넷 친구 테스트 (자동 서버 승계)'
    Write-Host '[9] 구형 월드 저장을 새 폴더로 변환'
    Write-Host '[0] 종료'
    do {
        $choice=Read-Host '실행할 서버 번호'
        switch($choice) {
            '1' {$Scenario='normal'}
            '2' {$Scenario='m3-demo'}
            '3' {$Scenario='m4-demo'}
            '4' {$Scenario='m5-demo'}
            '5' {$InternetTest=$true}
            '9' {$MigrateWorld=$true}
            '0' {exit 0}
            default {Write-Host '0~5 또는 9 중 하나를 입력하세요.'}
        }
    } while($choice -notin @('1','2','3','4','5','9'))
    if(!$InternetTest) { Write-Host "선택한 서버: $Scenario" }
}
$clientRoot=Split-Path $PSScriptRoot -Parent
$repositoryRoot=Split-Path $clientRoot -Parent
if($MigrateWorld) {
    if($Online -or $InternetTest -or $Scenario -like 'main-world-*'){throw '저장 변환은 서버·시험 모드와 함께 실행하지 않습니다.'}
    Write-Host '원본과 백업은 보존하고, 아직 존재하지 않는 별도 출력 폴더를 만듭니다. 먼저 서버를 종료하세요.'
    $migrationSource=Read-Host '원본 월드 파일이 직접 들어 있는 폴더'
    $migrationOutput=Read-Host '새 출력 폴더 (기존 폴더 사용 불가)'
    $sourceData=Read-Host '원본 저장과 해시가 일치하는 옛 gamedata.json 경로'
    $targetData=Join-Path $repositoryRoot 'shared/Data/gamedata.json'
}
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
$artifacts=if($InternetTest) {
    Join-Path $repositoryRoot '.artifacts/relay-test/build'
} else {
    Join-Path $clientRoot 'Temp/ServerRun/dotnet-artifacts'
}
$buildSettings=@('-p:UseArtifactsOutput=true',"-p:ArtifactsPath=$artifacts",'-m:1','-nodeReuse:false','-p:UseSharedCompilation=false')
Write-Host '최신 소스를 복원·Release 빌드합니다.'
& $dotnetPath $selected restore $project --locked-mode @buildSettings
if($LASTEXITCODE -ne 0){throw '서버 패키지 복원 실패'}
& $dotnetPath $selected build $project --no-restore -c Release @buildSettings
if($LASTEXITCODE -ne 0){throw '서버 빌드 실패'}
$serverDll=Join-Path $artifacts 'bin/ElementalLegion.Server/release/ElementalLegion.Server.dll'
if($BuildOnly){Write-Host "빌드 완료: $serverDll";exit 0}
if($InternetTest) {
    & (Join-Path $PSScriptRoot 'Start-InternetTest.ps1') -DotnetPath $dotnetPath -ServerDll $serverDll
    exit 0
}
Write-Host '서버 배너의 프로토콜 번호를 확인하세요. 최신 서버와 클라이언트를 함께 사용합니다.'
if($MigrateWorld) {
    $serverArgs=@('--migrate-world',$migrationSource,'--migration-output',$migrationOutput,'--source-data',$sourceData,'--target-data',$targetData)
} elseif($Online) {
    $serverArgs=@('--online','--save-dir',(Join-Path $repositoryRoot '.server-data/online'))
} else {
    $serverArgs=@('--listen','--scenario',$Scenario)
    if($Scenario -in @('normal','m5-demo')) {
        $serverArgs+=@('--save-dir',(Join-Path $repositoryRoot ".server-data/$Scenario"))
    }
    if($ReportNetwork){$serverArgs+='--report-network'}
    if($ReportGameplay){$serverArgs+='--report-gameplay'}
}
& $dotnetPath $serverDll @serverArgs
exit $LASTEXITCODE
