# 녹화 모드 구현 검증 — 2026-11-15

기존 작업 브랜치 `codex/match-dialog-buttons`의 `ca9b36c` 이후 변경이다. 사용자의 최종 지시에 따라 새 브랜치/복사본을 만들지 않았다. main 병합, Push, 설치본 교체, Publish 및 ZIP 생성은 수행하지 않았다.

## 실행 및 캡처 정책

`KoTrans2.exe --recording`을 시작 시에만 처리한다. 설정/프로필/UI 항목을 추가하지 않으며 프로세스 내 정책의 재설정을 거부한다. 창 생성 전에 정책을 초기화한다. 이미 실행 중인 프로세스로는 기존 활성화 요청만 전달하며 옵션을 전달하지 않는다. 두 번째 실행에서 녹화 옵션을 요청하면 완전 종료 후 다시 실행하라는 안내를 표시한다.

새 HWND는 기본 `WDA_NONE` 상태이므로 녹화 모드에서 캡처 제외 API를 호출하지 않는다. `CaptureExcluded`는 false로 유지하여 실제 상태와 OCR 가림 검사가 일치한다. 기존 API 직접 호출은 공통 정책 내부 한 곳으로 모았다.

| 실제 HWND 검사 | 일반 | Debug | Recording | Recording + Debug |
| --- | --- | --- | --- | --- |
| 자막 텍스트 | 0x11 제외 | 0x11 제외 | 0 허용 | 0 허용 |
| 별도 자막 툴바 | 0x11 제외 | 0x11 제외 | 0 허용 | 0 허용 |
| 문장 해설창 | 0x11 제외 | 0 허용 | 0 허용 | 0 허용 |
| 처음 사용하기 마법사 | 0x11 제외 | 0 허용 | 0 허용 | 0 허용 |
| 메인/설정창 | 0 허용 | 0 허용 | 0 허용 | 0 허용 |
| 디버그창 | 생성 안 함 | 허용 | 생성 안 함 | 허용 |

`GetWindowDisplayAffinity`로 각 창의 실제 상태를 확인했다. 해설창 닫기/다시 표시, 클릭 통과, 툴바 입력 가능 상태, 프로필 저장/읽기, 정상 종료도 통과했다. 일반 설정·모델 선택·로컬 AI 준비/확인·사전 도움말·후원 안내·영역 선택 창은 기존부터 캡처 제외를 적용하지 않는다. 기존 동작을 유지했다.

## OCR 자기 인식 방어

- 게임 원본 캡처의 `WindowPrintCapture`/`PrintWindow` 경로를 유지했다. 원본 캡처 실패 후 데스크톱 캡처로 자동 우회하지 않는다.
- 화면 영역/GDI/Magpie 화면 캡처 및 마커 관측의 기존 `EnsureCaptureSafe` 검사를 유지했다. 자동/수동 OCR 모두 같은 캡처 경로를 사용하며 CPU/GPU OCR 선택과 독립적이다.
- 녹화 모드에서 자막 텍스트·툴바·해설창이 인식 영역을 가리면 예상된 가림 대기로 처리한다. 창을 영역 밖으로 옮기거나 숨기라는 안내를 제공한다.
- WPF `Application.Windows`에 없는 ToolTip/Popup도 녹화 모드에서 현재 프로세스의 HWND를 열거하여 검사한다. 실제 합성 팝업을 겹치면 차단하고, 숨기면 해제되는 것을 확인했다.
- Magpie 원본 캡처 설정, 전경 게임/확대 화면의 가림·순서·좌표 검증은 변경하지 않았다. OCR 모델/알고리즘/개별 잡음 예외를 변경하지 않았다.

## 검사 결과

| 검사 | 결과 | 시간 |
| --- | --- | --- |
| Work -BuildOnly (Release, 최종) | PASS, 경고/오류 0 | 6.21초 |
| Wpf Background | PASS, 5,590 assertions, 표시/네이티브 창 0 | 73.92초 |
| Foundation | PASS | 5.66초 |
| Ocr | PASS, CPU/GPU 정책·IPC·fallback 등의 Mock 검사 | 3.94초 |
| Translation | PASS, 자동/수동·가림 대기/복구·Debug 회귀 포함 | 129.29초 |
| GameQa | PASS, 107 assertions | 4.02초 |
| Desktop Normal | PASS, 16 assertions | 8.77초 |
| Desktop Debug | PASS, 18 assertions | 6.83초 |
| Desktop Recording | PASS, 18 assertions | 6.69초 |
| Desktop RecordingDebug | PASS, 20 assertions | 7.01초 |

Wpf에는 4가지 인자 조합, 독립 테스트 설정과의 조합, 정책 고정, 재실행 안내 및 실제 별도 프로세스가 양쪽 옵션을 요청해도 기존 인스턴스의 정책이 변하지 않는 검사를 추가했다. 실제 창 검사는 독립 프로세스/합성 설정·문장만 사용했다. 유료 API와 사용자 설정/키를 사용하지 않았다.

초기 검사에서는 새 테스트의 임시 폴더 사전 생성 가정과 메인창 X의 트레이 숨김 동작을 잘못 가정한 정리 절차를 수정했다. Wpf 실행 중 Desktop 빌드를 겹쳐 발생한 테스트 실행 파일 잠금은 순차 실행으로 해소했다. 위 표는 보완 후 완료된 검사 결과다.

## OBS 확인과 범위

실행 중인 OBS Studio 33.0.1의 기존 **디스플레이 캡쳐** 소스 미리보기에서 녹화 모드의 실제 WPF 합성 자막, 별도 툴바, 문장 해설창이 표시되는 것을 직접 확인했다. 이후 별도 일반 모드 프로세스를 시작해 같은 창들이 OBS 미리보기에서 제외되는 것도 확인했다. OBS 장면/소스/녹화 설정은 변경하지 않았다.

- OBS 디스플레이 캡처 미리보기: **PASS**.
- OBS 녹화 파일 저장/재생, 게임 캡처 소스: **NOT RUN**. 이번 검증은 디스플레이 캡처에 UI가 포함되는지까지 확인했다.
- 실제 게임 연속 플레이, 실물 CPU/GPU OCR, Magpie 실게임: **NOT RUN**. 기존 캡처 경로 검토와 합성/Mock 회귀 검사를 수행했다.
- 발견된 미해결 구현 오류는 없다. 위 미실행 범위를 검증 완료로 간주하지 않는다.

사용자 확인 순서: 앱을 알림 영역에서 완전히 종료 → `KoTrans2.exe --recording` 실행 → 자막/해설을 OCR 영역 밖에 배치 → OBS 디스플레이 캡처에서 표시 확인 → 짧게 녹화 후 파일 재생. 옵션 없이 다시 실행하면 기존 제외 정책으로 돌아간다.

## 변경 파일

- 실행 옵션: `src/KoTrans.Core/AppLaunchOptions.cs`, `src/KoTrans.App/App.xaml.cs`
- 캡처 정책/창: `src/KoTrans.App/CaptureExclusionPolicy.cs`, `PreviewWindows.cs`, `OverlayChromeWindow.cs`, `FirstRunWizardWindow.cs`, `DebugOverlayWindow.cs` (모두 `src/KoTrans.App`)
- OCR 가림 보호: `src/KoTrans.App/MainWindow.Translation.cs`, `src/KoTrans.Infrastructure/WindowTargets.cs`
- 검증: `tests/KoTrans.WpfTests/RecordingModeTests.cs`, `Program.cs`, `InstanceAndSettingsPolishTests.cs` (동일 테스트 폴더), `scripts/Test-Desktop.ps1`
- 문서: `docs/development.md`, `docs/recording-mode.md`, 이 보고서

Single-file Publish, DLL 배치, OpenCV, OCR 모델, Local AI, 라이선스, Portable 정책과 버전 번호에는 변경이 없다. 정식 배포 ZIP은 이후 승인된 커밋에서 다시 빌드해야 한다.
