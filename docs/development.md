# 개발 환경

Windows 10/11 x64, .NET 10 SDK (`global.json`), PowerShell 7, Git을 준비한다. 네이티브 OpenCV 빌드에는 Visual Studio 2026의 C++ 데스크톱 개발 워크로드, Windows SDK, CMake 3.20 이상이 필요하다. NuGet 복원에는 인터넷 연결이 필요하다.

```powershell
pwsh -NoProfile -File scripts/Prepare-OpenCv.ps1
dotnet restore KoTrans.slnx
dotnet build KoTrans.slnx -c Release
Dev-Run.cmd
```

`Prepare-OpenCv.ps1`은 고정된 OpenCV 4.11.0과 OpenCvSharp 4.11.0.20250507 소스 아카이브의 SHA-256을 확인한다. OCR에 사용하는 core, imgproc, imgcodecs를 IPP/IW 없이 정적으로 링크하고 `artifacts/opencv/win-x64`에 저장한다. CPU 기본 요구사항은 x64 SSE2이며, 추가 최적화는 런타임에 CPU 기능을 감지해 사용한다. 네이티브 결과물과 모델 파일은 로컬에 보관한다.

`Dev-Run.cmd`는 개발용 앱을 증분 빌드하고 실행한다. `main`만 있는 신규 체크아웃도 사용할 수 있다. 기존 `codex/*` 작업 트리는 `-Branch` 또는 `-Choose`로 선택한다. 원격 작업 브랜치 동기화는 [Sync-DevRun](sync-dev-run.md)을 사용한다. 설치본은 `Run.cmd`로 실행한다.

```powershell
pwsh -NoProfile -File scripts/Work.ps1 -BuildOnly
pwsh -NoProfile -File scripts/Test-Fast.ps1 -Suite Wpf
pwsh -NoProfile -File scripts/Test-Fast.ps1 -Suite Ocr
```

앱을 닫을 때는 알림 영역 아이콘의 **종료**를 사용한다. 실화면 검증은 `scripts/Test-Desktop.ps1 -List`로 사용 가능한 검사를 확인한 뒤 격리된 환경에서 실행한다.

`KoTrans2.exe --recording`은 시작 시에만 UI 캡처 제외를 해제한다. 실행 옵션 조합과 OCR 가림 보호는 [녹화 모드](recording-mode.md)를 참고한다.

- [모델 준비](models.md) · [구성](architecture.md) · [개발 및 테스트 정책](branch-test-policy.md)
- [정식 Publish와 설치](release.md) · [Portable ZIP](portable-release.md)
