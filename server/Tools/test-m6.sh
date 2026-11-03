#!/usr/bin/env bash
# 전용 Linux 테스트 VM에서 두 접속 경로를 순서대로 측정한다.
set -euo pipefail
umask 077
if [[ $(uname -s) != Linux || $EUID != 0 ]]; then
    echo '전용 Linux 테스트 VM에서 관리자 권한으로 실행하세요.' >&2
    exit 2
fi

profile=${1:-all}
if [[ $# -gt 1 || ! $profile =~ ^(all|baseline|netem)$ ]]; then
    echo '사용법: bash server/Tools/test-m6.sh [all|baseline|netem]' >&2
    exit 2
fi
for tool in dotnet ip tc ss openssl ethtool; do command -v "$tool" >/dev/null; done

cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.."
repo=$PWD
mkdir -p .artifacts/m6
results=$(mktemp -d "$repo/.artifacts/m6/linux.XXXXXXXX")
export M6_SERVER_NS="m6-server-$$"
client_ns="m6-client-$$"
export M6_SERVER_DLL="$repo/server/Server/bin/Release/net10.0/ElementalLegion.Server.dll"
bots_dll="$repo/server/Bots/bin/Release/net10.0/ElementalLegion.Bots.dll"
server_address=10.23.6.1
export NO_PROXY="$server_address" no_proxy="$server_address"
server_pid=
server_created=false
client_created=false
failed=0

stop_server() {
    if [[ -n $server_pid ]]; then
        kill -TERM "$server_pid" 2>/dev/null || true
        wait "$server_pid" 2>/dev/null || true
        server_pid=
    fi
}

cleanup() {
    stop_server
    for namespace in "$client_ns" "$M6_SERVER_NS"; do
        if [[ $namespace == "$client_ns" && $client_created == false || $namespace == "$M6_SERVER_NS" && $server_created == false ]]; then continue; fi
        # 이 실행이 만든 격리 공간에서 남은 테스트 프로세스만 정리한다.
        for pid in $(ip netns pids "$namespace"); do kill -TERM "$pid" 2>/dev/null || true; done
        ip netns del "$namespace"
    done
    rm -f -- "$results/ca.key" "$results/server.key" "$results/server.pfx"
    printf '결과 폴더: %s\n' "$results"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

dotnet build server/ElementalLegion.sln -c Release -p:RestoreLockedMode=true -m:1 -nodeReuse:false
ip netns add "$M6_SERVER_NS"
server_created=true
ip netns add "$client_ns"
client_created=true
ip link add "m6s$$" netns "$M6_SERVER_NS" type veth peer name "m6c$$" netns "$client_ns"
ip -n "$M6_SERVER_NS" link set "m6s$$" name link0
ip -n "$client_ns" link set "m6c$$" name link0
ip -n "$M6_SERVER_NS" addr add "$server_address/30" dev link0
ip -n "$client_ns" addr add 10.23.6.2/30 dev link0
for namespace in "$M6_SERVER_NS" "$client_ns"; do
    ip -n "$namespace" link set lo up
    ip -n "$namespace" link set link0 up
    ip netns exec "$namespace" ethtool -K link0 gro off gso off tso off
done

openssl req -new -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=Elemental Legion M6 CA' \
    -addext 'basicConstraints=critical,CA:TRUE' -addext 'keyUsage=critical,keyCertSign' \
    -keyout "$results/ca.key" -out "$results/ca.pem" 2>"$results/openssl.log"
openssl req -new -newkey rsa:2048 -nodes -subj "/CN=$server_address" \
    -keyout "$results/server.key" -out "$results/server.csr" 2>>"$results/openssl.log"
printf 'subjectAltName=IP:%s\nextendedKeyUsage=serverAuth\n' "$server_address" >"$results/server.ext"
openssl x509 -req -days 1 -in "$results/server.csr" -CA "$results/ca.pem" -CAkey "$results/ca.key" -CAcreateserial \
    -extfile "$results/server.ext" -out "$results/server.pem" 2>>"$results/openssl.log"
export ELEMENTAL_LEGION_CERT_PATH="$results/server.pfx"
export ELEMENTAL_LEGION_CERT_PASSWORD
ELEMENTAL_LEGION_CERT_PASSWORD=$(openssl rand -hex 16)
openssl pkcs12 -export -in "$results/server.pem" -inkey "$results/server.key" -out "$ELEMENTAL_LEGION_CERT_PATH" \
    -passout env:ELEMENTAL_LEGION_CERT_PASSWORD

cat >"$results/host-room" <<'HOST'
#!/usr/bin/env bash
# 방장과 중계를 같은 망에 두고 참가자 링크만 지연시킨다.
exec ip netns exec "$M6_SERVER_NS" dotnet "$M6_SERVER_DLL" "$@"
HOST
chmod +x "$results/host-room"

run_case() {
    local transport=$1 condition=$2 name=$3 bots=$4 warmup=$5 duration=$6
    shift 6
    local directory="$results/$name-$transport"
    local -a endpoint
    mkdir "$directory"
    printf '%s %s %s\n' "$name" "$transport" "$bots"
    if [[ $transport == udp ]]; then
        ELEMENTAL_LEGION_LOAD_TRACE="$directory/ticks.ndjson" ip netns exec "$M6_SERVER_NS" dotnet "$M6_SERVER_DLL" \
            --listen --port 7777 --save-dir "$directory/store" >"$directory/server.log" 2>&1 &
        endpoint=(--host "$server_address" --port 7777 --server-trace "$directory/ticks.ndjson")
    else
        ip netns exec "$M6_SERVER_NS" dotnet "$M6_SERVER_DLL" --online --port 8443 \
            --save-dir "$directory/store" >"$directory/server.log" 2>&1 &
        endpoint=(--relay "https://$server_address:8443" --host-server "$results/host-room")
    fi
    server_pid=$!
    local ready=false
    for ((attempt=0; attempt<150; attempt++)); do
        if [[ $transport == udp && -s $directory/ticks.ndjson ]] ||
            { [[ $transport == wss ]] && ip netns exec "$M6_SERVER_NS" ss -Hlt 'sport = :8443' | read -r _; }; then
            ready=true
            break
        fi
        if ! kill -0 "$server_pid" 2>/dev/null; then break; fi
        sleep 0.1
    done
    if [[ $ready != true ]]; then
        printf '서버 준비 실패: %s\n' "$directory/server.log" >&2
        failed=1
    elif ! SSL_CERT_FILE="$results/ca.pem" ip netns exec "$client_ns" dotnet "$bots_dll" \
        --bots "$bots" "${endpoint[@]}" --seed 1 --profile "$condition" --warmup-seconds "$warmup" \
        --duration-seconds "$duration" --report "$directory/result.json" "$@"; then
        failed=1
    fi
    stop_server
    if [[ $condition == netem ]]; then
        ip netns exec "$M6_SERVER_NS" tc -s qdisc show dev link0 >"$directory/netem-server.txt"
        ip netns exec "$client_ns" tc -s qdisc show dev link0 >"$directory/netem-client.txt"
    fi
}

run_case udp baseline smoke 1 0 3
run_case wss baseline smoke 2 0 3
run_case udp baseline reconnect 15 0 5 --reconnect
run_case wss baseline reconnect 15 0 5 --reconnect
if [[ $profile != netem ]]; then
    for iteration in 1 2 3; do
        run_case udp baseline "baseline-$iteration" 15 30 300
        run_case wss baseline "baseline-$iteration" 15 30 300
    done
fi
if [[ $profile != baseline ]]; then
    for iteration in 1 2 3; do
        for transport in udp wss; do
            ip netns exec "$M6_SERVER_NS" tc qdisc replace dev link0 root netem delay 40ms 10ms loss random 1% seed 1
            ip netns exec "$client_ns" tc qdisc replace dev link0 root netem delay 40ms 10ms loss random 1% seed 2
            run_case "$transport" netem "netem-$iteration" 15 30 300
        done
    done
fi
exit "$failed"
