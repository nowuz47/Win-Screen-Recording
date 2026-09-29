# Glide — Windows 화면 녹화·발표

커서 중심 확대, 녹화, 실시간 발표 및 간단한 영상 편집을 지원하는 Windows 데스크톱 앱입니다. C# / WinUI 3와 C++ 캡처 엔진으로 구현합니다.

**개발 프리뷰입니다. Production Ready 상태가 아닙니다.** 공개 빌드는 서명되지 않았으며 장시간 녹화, 실기기 GPU·오디오, 접근성, 외부 회의 앱 공유와 배포 검증이 남아 있습니다. 최신 포인터 드래그 변경은 빌드 검증 후 실제 UI 회귀 확인이 필요합니다.

- 녹화 / 발표 모드와 커서 중심 1.5배 기본 확대
- 이동 가능한 반투명 제어 패널과 발표 보조 화면
- 마이크·시스템 소리 분리 저장, 영상 구간 편집 및 MP4 내보내기
- 확대·전환 애니메이션과 모션 감소 설정

## 다운로드 및 실행

GitHub Releases에서 `Glide-Setup-0.1.0-preview.3-x64.exe`를 실행해 설치합니다. 기본 설치 위치는 `%LOCALAPPDATA%\Programs\Glide`이며 관리자 권한 없이 현재 사용자에게 설치하고 시작 메뉴에 등록합니다. 바탕 화면 바로 가기는 선택할 수 있습니다. Windows 설정의 설치된 앱에서 제거할 수 있으며 녹화 및 설정 데이터는 보존합니다. 업데이트 전에는 기존 Glide를 종료하세요. 설치 프로그램은 Windows 11 이상을 대상으로 합니다.

설치 없이 사용하는 경우 ZIP을 풀고 `Glide.App.exe`를 실행합니다. Windows x64용이며 Windows 11을 검증 대상으로 합니다. Windows on ARM의 x64 에뮬레이션 환경에서 개발 검증을 수행했지만 모든 장치의 호환성을 보장하지 않습니다. 코드 서명은 아직 제공하지 않습니다.

## 소스 빌드

Windows에서 .NET SDK 10.0.401, Visual Studio 2022 이상 C++ 빌드 도구, Windows SDK 10.0.26100 및 CMake 3.25 이상이 필요합니다. Visual Studio의 C++ 데스크톱 개발 구성 요소를 설치합니다.

```powershell
cmake -S . -B build/native -A x64
cmake --build build/native --config Release
dotnet publish src/Glide.App -c Release -r win-x64 -p:Platform=x64 -o out/app
Copy-Item build/native/Release/Glide.Capture.dll out/app/Glide.Capture.dll
& out/app/Glide.App.exe
```

설치 EXE를 만들려면 Inno Setup 6.3 이상을 설치한 뒤 `powershell -File scripts/Build-Installer.ps1`을 실행합니다. 설치 스크립트는 [installer/Glide.iss](installer/Glide.iss)에 있습니다.

GitHub Actions가 같은 소스의 네이티브 엔진·WinUI 앱·테스트를 빌드하고 `glide-windows-x64` 산출물을 보관합니다. `scripts/Build-App.cmd` 등의 기존 개발 스크립트는 로컬 VM 전용 SDK 경로를 사용하므로 일반 환경에서는 위 명령을 사용합니다.

## 검증 범위

2026-09-26 로컬 Windows 검증에서 네이티브 엔진 빌드, 네이티브 시간축·오디오 큐 검사, 발표 회귀 6개와 WinUI 게시 빌드가 통과했습니다. 검증 실행기 자체의 중단·종료 코드 검사도 4개 통과했습니다. 이는 프레임 품질·성능이나 정식 출시 판정을 대체하지 않습니다.

[최근 UI 개선](docs/15-ux-refresh.md), [후속 개선 계획](docs/16-next-improvements.md), [출시 기준](docs/04-production-readiness.md)을 함께 확인하세요. `docs/validation`의 과거 증거는 개인 경로를 익명화한 사본입니다. 문서의 과거 실측은 해당 후보에만 적용됩니다.

## 설계 및 개발 기록

| 문서 | 검토할 내용 |
| --- | --- |
| [제품 기획과 사용자 경험](docs/01-product-and-ux.md) | 대상 사용자, 핵심 가치, 화면 흐름, 출시 범위, 동작 원칙 |
| [기술 설계](docs/02-architecture.md) | Windows 엔진, 커서 분리, 자동 확대, 저장·복구, 공유 구조 |
| [개발 순서와 검증 기준](docs/03-delivery-plan.md) | 먼저 확인할 기술 위험, 단계별 완료 조건, 품질 지표 |
| [Production Ready 기준](docs/04-production-readiness.md) | 정식 출시를 위한 12개 필수 게이트, 측정 조건, 차단 결함, 운영 대응 |
| [출시 후보 판정표](docs/release-readiness-template.md) | 빌드별 증거·실측값·담당자·최종 출시 판단 기록 |
| [개발 현황과 실행 방법](docs/05-development-status.md) | 구현 범위, 실제 테스트 결과, Windows 검증 재개 방법, 남은 작업 |
| [두 모드 UI·UX와 구현 현황](docs/06-two-mode-implementation.md) | 확정한 모드 동작, 코드 경계, 검증 범위와 재개 명령 |
| [두 모드 추가 출시 기준](docs/07-two-mode-release-gates.md) | 청중 출력·가림·지연·세션 분리·접근성의 추가 필수 기준 |
| [UI 모션 구현과 검증](docs/08-ui-motion.md) | 화면별 모션, 감소 설정, Windows 실행 결과, 성능·접근성 출시 조건 |
| [Production Ready 개발 기록](docs/09-production-worklog.md) | 캡처 원인 계측·검사 보강·후속 개발 순서와 실제 증거 |
| [오디오 구현과 검증](docs/10-audio-pipeline.md) | 두 트랙·복구·편집·AAC·재생과 실제 음질 실패 증거 |
| [오디오 음정·시계 원인 분리](docs/11-audio-clock-investigation.md) | 원시 PCM과 장치 시계 계측, 연속 보정, 남은 무음·동기화 실패 |
| [오디오 수집·저장 분리](docs/12-audio-storage-queue.md) | 제한된 큐, 저장 장애 주입, 원본 보존과 복구 상태 검증 |
| [캡처 프레임 재사용](docs/13-capture-frame-reuse.md) | 중복 GPU 읽기 제거, 프레임·QPC 대조와 실제 UI 녹화 검증 |
| [중복 실행·종료 안정성](docs/14-instance-lifecycle.md) | 단일 프로필 소유권, 창 복원, 전역 종료 단축키와 실패 시 검증 중단 |

우선 추천하는 방향은 다음과 같습니다.

- **주요 사용자:** 짧은 서비스 데모를 만드는 기획자·개발자·디자이너. 긴 강의와 게임 녹화는 후순위입니다.
- **핵심 경험:** 대상 선택 → 녹화 → 자동 정리된 미리보기 → 필요한 부분만 수정 → MP4 저장 또는 링크 공유.
- **핵심 구조:** 커서 없는 원본 영상, 커서·클릭 데이터, 음성, 편집 설정을 각각 저장합니다.
- **기술 후보 1순위:** C# / WinUI 3 UI와 C++ / WinRT 미디어 엔진. Windows 전용 품질을 우선하고 네이티브 개발 역량을 확보할 수 있다는 가정입니다.
- **검증 순서:** 30초 실제 녹화에서 커서 분리·동기화·확대 품질을 먼저 확인한 뒤 전체 편집기를 만듭니다.
- **출시 순서:** 내부 검증에서는 파일 저장으로 전체 흐름을 확인하고, 공개 첫 버전에는 링크 공유까지 포함합니다.

확정되지 않은 사항은 팀의 기술 역량, 일정·예산, Windows 10 지원 필요성, 웹캠 우선순위, 클라우드 운영 범위입니다. 문서의 성능 수치와 일정은 실측 결과가 아닌 초기 목표·추정입니다.

정식 출시에는 [Production Ready 기준 v1.0](docs/04-production-readiness.md)을 적용합니다. 기존 개발 문서의 탐색용 목표와 충돌할 경우 정식 출시 판정은 이 기준을 우선합니다. 지원 환경 확정, G00~G11 전부 PASS, P0·P1 미해결 0건, 실제 배포물의 검증 증거가 필요합니다. 코어와 Windows의 짧은 기능 검증을 수행했으며 출시 게이트는 통과하지 않았습니다.

참고 서비스에서 확인한 제품 방향은 자동 확대, 부드러운 커서, 배경 편집, 내보내기·공유입니다. 아래 문서는 이 기능을 Windows에서 구현하기 위한 독립적인 제안이며, 해당 서비스의 내부 구현을 설명하지 않습니다. [Screen Studio 공식 소개](https://screen.studio/)
