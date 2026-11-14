# Background / WPF / Desktop 개발 정책

2026-10-29 도입한 Background 기본 정책을 유지한다. main 병합 전의 작업은 기존 미병합 codex 브랜치를 유지하고, 2026-10-30 main 반영 이후 새 요청은 최신 main에서 새 codex/* 브랜치로 시작한다. 개발 요청만으로 main 병합·설치가 승인되지는 않는다.

## 일상 Codex 작업

1. 관련 소스만 수정한다.
2. `pwsh -NoProfile -File scripts/Work.ps1 -BuildOnly`로 앱과 참조 프로젝트를 증분 빌드한다.
3. 변경과 관련 있는 Background FAST만 실행한다. WPF 변경은 `-Suite Wpf`를 선택한다.
4. 필요한 최소 검증 성공 후 이번 파일만 선택적으로 commit하고 승인된 PRIVATE 원격의 같은 작업 브랜치로 non-force push한다.
5. 변경·검증 시간·제한·commit/push 결과를 짧게 보고한다.

이 과정은 GUI DEV-RUN·Desktop 테스트·Computer Use를 호출하지 않는다. Git은 Codex의 작업 마무리에서만 실행하고 Work/DEV-RUN/테스트/hook에 넣지 않는다. 실행 중인 앱의 파일 충돌 방어와 강제 종료 금지는 유지한다.

## Background FAST 선택

```powershell
pwsh -NoProfile -File scripts/Test-Fast.ps1 -Suite Wpf
pwsh -NoProfile -File scripts/Test-Fast.ps1 -Suite Study
pwsh -NoProfile -File scripts/Test-Fast.ps1 -Suite Translation
```

| Suite | 범위 |
|---|---|
| Foundation | 기존 Image + Settings |
| Image | 이미지 경계·stride·취소·미설정 OCR |
| Settings | 임시 설정의 원자 저장·복원·손상 보존 |
| Ocr | 모델 없는 CTC·레이아웃·변화·ROI 알고리즘 |
| Translation | Mock HTTP·합성 DPAPI 키·문맥·중복·파이프라인 취소 |
| Study | JSON·Mock 제공자·취소·메모리 캐시·합성 LNVI·로컬 IPA·자원 수명·설정 호환 |
| Stability | 발음 인덱스 캐시·잠긴 임시 MP3 정리·모델 경로/무결성·실제 ROI 전달·50회 Mock 파이프라인 수명 (모델/사전 없음) |
| GameQa | 합성 PNG·완료/진행/취소·실패 상태·프레임/번역 연결·ROI/fallback·저장 재시도·모델 식별·오프라인 재현 Mock (실모델/화면 없음) |
| Wpf | 실제 학습창/번역창 컨트롤·설정·프로필·Dispatcher의 오래된 결과 폐기 |

기존 `KoTrans.Tests` 콘솔 검사와 assertion을 보존한다. 외부 테스트 프레임워크 없이 실행하며 실패는 nonzero exit code다. 잘못된 suite는 오류이며 0개 검사 성공으로 처리하지 않는다.

`KoTrans.WpfTests`는 별도의 작은 C# 실행기다. `[STAThread]`, `Application`, `DispatcherSynchronizationContext`를 사용하고 실제 앱의 공용 Styles와 컨트롤을 참조한다. 앱의 `OnStartup`은 실행하지 않는다. 컨트롤은 `Measure/Arrange/UpdateLayout`, 논리 이벤트, 상태 속성으로 검사한다. Window는 표시하지 않고 `SourceInitialized`/visibility 및 Loaded 방어로 창 HWND/표시를 감지한다. 종료 시 창·Mock 작업·Dispatcher를 정리한다.

Background에서는 실제 마우스·키보드·포커스·클립보드·화면 캡처를 사용하지 않는다. 임시 디렉터리의 합성 설정과 Mock 서비스만 사용하며 사용자 설정/키나 외부 AI에 접근하지 않는다. 화면 없는 검사는 실제 키 전달·게임 포커스·DPI 성공을 증명하지 않는다.

각 FAST 명령은 빌드+실행 시간을 출력한다. Wpf는 시나리오별 시간, Study는 JSON/HTTP/세션/인덱스/사전/수명/설정 시간을 출력한다. Study의 5초 timeout 검사는 의도적으로 기다리며 작은 UI 수정의 필수 검사가 아니다. Wpf는 실제 OCR/MeCab 사전 로딩, 모델 다운로드·변환, 음성 재생을 하지 않는다. 기존 Study의 symlink 권한 부족은 SKIP으로 표시하며 별도 합성 junction fixture를 `KOTRANS_TEST_VOICE_JUNCTION`에 지정할 수 있다.

## 명시적인 GUI DEV-RUN

```powershell
pwsh -NoProfile -File scripts/Work.ps1
# 또는 Dev-Run.cmd
pwsh -NoProfile -File scripts/Work.ps1 -TestData E:\some-qa-folder -MockAi
```

사용자가 직접 실행하거나 요청했을 때만 호출한다. 기존 `Dev-Run.cmd`/`Dev-Run.ps1`/Work의 GUI 동작은 유지한다. `-BuildOnly`는 GUI를 생략한다. 기존 개발/정식 설정과 단일 실행 잠금도 유지한다. `-TestData`는 합성 설정용 QA 경로, `-MockAi`는 모의 번역/학습이며 실제 OCR을 사용하는 GUI QA와 순수 Background를 혼동하지 않는다.

## 명시적인 Desktop 검사

```powershell
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -List # 목록만, 창 표시 없음
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite WindowLifecycle
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite DictionaryHoverCapture # 명시적 요청: 툴팁 on/off 실제 캡처 A/B
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite StudyResize # 명시적 요청: 서브 모니터 테두리·실제 커서 진단, 마우스 이동
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite WindowResize # 명시적 요청: 메인/설정 테두리 회귀, 격리된 Edge 사전창 관측
```

실행은 사용자의 명시적 Desktop 요청이 있을 때만 한다. 같은 C# WPF 실행기의 `--desktop WindowLifecycle`은 실제 학습·번역창 Show/Hide/복원과 학습 원격 스크롤을 검사한다. `OpenInputDesktop`/입력 데스크톱 이름/`GetCursorPos` 사전 확인이 실패하면 **ENVIRONMENT UNAVAILABLE, exit 3**으로 보고한다. 기능 assertion 실패는 **FAIL, exit 1**이다. 환경 오류를 성공으로 바꾸지 않는다. `-List`는 실행 승인이 아니다.

실제 마우스·키 입력/전역 단축키, 창 포커스/입력 전달, GDI/Windows Graphics Capture, 오버레이 캡처 제외, 실물 게임패드, MP3 청취/종료, 다중 모니터·혼합 DPI, 게임 연속 조작은 Desktop 범위다. WindowLifecycle, OverlayDrag 및 아래의 DictionaryHoverCapture가 담당하는 범위 외에는 명시적 수동/추가 C# Desktop 검사 대상이다. Background 또는 일부 Desktop 검사 통과로 전체 Desktop 동작을 검증했다고 주장하지 않는다. Computer Use를 호출하는 경로는 없다.

후속으로 추가한 `DictionaryHoverCapture`는 고정 합성 화면에서 실제 TranslationWindow의 캡처 제외와 별도 툴팁의 GDI 픽셀 변화, 기존 TranslationPipeline의 추가 Mock OCR 호출을 비교한다. 실제 화면을 사용하므로 사용자 명시 요청이 필요하다. 게임 입력·이미지 저장·실제 OCR 모델·유료 AI는 사용하지 않는다. 일반 Wpf FAST에서는 실행하지 않는다.

`StudyResize`는 실제 서브 모니터에서 StudyWindow·기본 WPF/Win32 창의 프레임 판정과 커서 유지 상태를 비교한다. 마우스를 잠시 이동하고 복원하며 클릭/드래그·게임·캡처·사용자 설정은 사용하지 않는다. 테스트 실행기는 제품과 같은 PerMonitorV2 manifest를 사용한다. 일반 Background 검사에는 연결하지 않는다.

## 향후 main 병합

사용자가 main 병합을 요청했을 때만 전체 Background 회귀와 필요한 Desktop, 정식 Publish·설치 검증을 준비하고 최종 사용자 승인 후 병합한다. `Work.ps1 -Action MainMerge`는 명시적으로 요청한 경우에만 전체 Rebuild와 Background 회귀를 실행한다. `-Desktop`은 별도 C# WindowLifecycle과 격리된 Publish 구성을 확인하며 새 출력 경로와 NMeCab 대응 소스가 필요하다. `-Approve`는 Git 병합/설치를 하지 않고 별도 사용자 승인 절차를 안내한다. 자세한 명령과 남은 검증은 [릴리즈 준비](release.md)를 따른다.
