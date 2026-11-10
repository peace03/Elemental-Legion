# 설치 도구 목록

Agent Desktop은 2026-11-09에 설치·확인했다. 나머지 도구의 버전은 2026-11-03에 확인한 기록이며, 프로젝트가 지정한 버전은 연결된 설정 파일을 기준으로 한다.

| 도구 | 확인한 버전 | 용도 | 실행 위치·설정 |
| --- | --- | --- | --- |
| Agent Desktop | 1.2.0 | Windows 앱의 접근성 정보를 읽고 화면 요소를 조작 | 전역 Codex MCP 서버 `agent-desktop`로 등록. CLI는 `agent-desktop.cmd status`로 확인. [설치·검증 내역](third-party.md#agent-desktop-2026-11-09) |
| Graphify | 0.9.63 | C# 호출 관계와 변경 영향 탐색 | 저장소 루트에서 `graphify` 실행. [조회·갱신 방법](Rules/graphify.md) |
| PixelFX | 0.3.0 | 픽셀 이펙트를 PNG 스프라이트 시트와 프레임 JSON으로 생성 | `client/Tools/PixelFX/`에 설치. [내보내기 방법](third-party.md#pixelfx-2026-11-03) · [잠금 파일](../client/Tools/PixelFX/package-lock.json) |
| Unity Editor | 6000.6.0f1 | 클라이언트 개발·임포트·테스트 | Unity Hub에서 `client/` 열기. [프로젝트 버전](../client/ProjectSettings/ProjectVersion.txt) |
| Unity Hub | 3.22.5 | Unity Editor와 프로젝트 관리 | Windows 시작 메뉴에서 Unity Hub 실행 |
| .NET SDK | 10.0.401 | 서버 빌드·실행·테스트 | 저장소 루트에서 `dotnet` 실행. [SDK 설정](../global.json) |
| Node.js | 24.21.0 | PixelFX와 Taste Skill 실행 | `node --version` |
| npm | 11.19.0 | PixelFX 설치·복원 및 내보내기 명령 실행 | `npm.cmd --version` |
| uv | 0.12.14 | Graphify 설치·버전 관리 | `uv --version` |
| Python | 3.14.7 | 프로젝트 파일과 생성 결과 확인 | `python --version` |
| PowerShell | 7.6.5 | Windows 개발 명령과 스크립트 실행 | `$PSVersionTable.PSVersion` |
| Git | 2.55.0.windows.5 | 저장소 버전 관리 | `git --version` |

## Codex 사용자 스킬

| 스킬 | 확인한 버전 | 용도 | 실행·설치 위치 |
| --- | --- | --- | --- |
| Agent Desktop | 1.2.0 · `2b0a733` | 데스크톱 앱을 관찰하고 조작하는 절차와 결과 확인 | Codex에서 `$agent-desktop` 사용. `~/.codex/skills/agent-desktop/`에 설치. Windows용 안내는 `agent-desktop.cmd skills get platform`으로 조회. [고정 소스](https://github.com/vercel-labs/agent-desktop/tree/2b0a7334fc111730454b726e6800aa41cdafc093/skills/agent-desktop) |
| Taste Skill | v2 · `12af471` | 웹페이지·앱 화면 디자인과 브라우저 검토 | Codex에서 `$design-taste-frontend` 사용. `~/.codex/skills/design-taste-frontend/`에 설치. [고정 소스](https://github.com/Leonxlnx/taste-skill/tree/12af471f627f866411b4bd785b19646d3672784c/skills/taste-skill) · [MIT 라이선스](https://github.com/Leonxlnx/taste-skill/blob/12af471f627f866411b4bd785b19646d3672784c/LICENSE) |

## 저장소에 포함된 실행 도구

| 도구 | 용도 |
| --- | --- |
| [Start-Server.cmd](../Start-Server.cmd) | 루트에서 실행해 서버를 빌드하고 선택한 시나리오로 시작. 5번은 친구 테스트용 중계와 임시 주소를 준비 |
| [Start-InternetTest.ps1](../client/Tools/Start-InternetTest.ps1) | Cloudflare Quick Tunnel로 테스트용 중계를 공개하고 Unity에 임시 주소를 전달. 고정 도구 버전과 출처는 [외부 자료 고지](third-party.md#cloudflare-quick-tunnel-2026-11-03) 참고 |
| [Publish-LanServer.ps1](../client/Tools/Publish-LanServer.ps1) | Windows x64 빌드 후 자동 호출돼 인터넷·LAN 방장용 서버를 포함 |

UPM·NuGet 버전은 각 잠금 파일, 외부 에셋과 라이선스는 [외부 자료 고지](third-party.md)에 기록한다. 도구를 추가하거나 버전을 변경하면 이 표와 확인일을 갱신한다.
