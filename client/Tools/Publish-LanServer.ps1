param([Parameter(Mandatory)][string]$PlayerPath)

$ErrorActionPreference = 'Stop'
$player = Get-Item -LiteralPath $PlayerPath
if ($player.PSIsContainer -or $player.Extension -ne '.exe') {
    throw 'Unity Windows Player 실행 파일을 지정하세요.'
}
$playerData = Join-Path $player.DirectoryName ($player.BaseName + '_Data')
if (!(Test-Path -LiteralPath $playerData -PathType Container)) {
    throw '실행 파일에 해당하는 Unity Player 데이터 폴더가 없습니다.'
}
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $repositoryRoot 'server/Server/ElementalLegion.Server.csproj'
$artifacts = Join-Path $repositoryRoot '.artifacts/room-server-publish'
$serverOutput = Join-Path $playerData 'Server'
New-Item -ItemType Directory -Force -Path (Join-Path $artifacts 'lock') | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true --artifacts-path $artifacts -o $serverOutput -p:RoomServerPublish=true -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $serverOutput 'ElementalLegion.Server.exe') -PathType Leaf)) {
    throw '방 서버 게시 실패'
}
Write-Host "방 서버 포함: $serverOutput"

$relayDirectory = Join-Path $repositoryRoot '.artifacts/relay-test'
$relayAddress = Join-Path $relayDirectory 'relay-test-url.txt'
$playerRelayAddress = Join-Path $playerData 'relay-test-url.txt'
$address = Get-Item -LiteralPath $relayAddress -ErrorAction SilentlyContinue
if ($address -and !$address.PSIsContainer -and $address.Length -le 128) {
    try {
        $stopped = [IO.File]::Open((Join-Path $relayDirectory 'relay.lock'), 'Open', 'Read', 'ReadWrite')
        $stopped.Dispose()
    } catch [IO.IOException] {
        # 실행기가 잠금을 잡고 있을 때만 주소를 복사한다.
        if (($_.Exception.GetBaseException().HResult -band 0xffff) -eq 32) {
            $url = ([string](Get-Content -LiteralPath $relayAddress -Raw -ErrorAction SilentlyContinue)).Trim()
            if ($url -cmatch '\Ahttps://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com\z') {
                Set-Content -LiteralPath $playerRelayAddress -Value $url -Encoding Ascii
                Write-Host '인터넷 친구 테스트 주소를 빌드에 포함했습니다.'
                exit 0
            }
        }
    }
}
if (Test-Path -LiteralPath $playerRelayAddress -PathType Leaf) { Remove-Item -LiteralPath $playerRelayAddress }
