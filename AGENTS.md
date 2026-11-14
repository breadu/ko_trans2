# KO Trans 2 개발 규칙

- Windows 10/11 x64용 .NET 10/WPF 앱이다. 기존 PaddleOCR, 설정 및 프로필 호환성을 유지한다.
- 사용자 설정·키·게임 화면과 대사는 Git 또는 로그에 올리지 않는다. 자동 검사에는 격리 설정과 합성 입력을 사용한다.
- 기본 개발 검증은 `scripts/Work.ps1 -BuildOnly`와 관련 `scripts/Test-Fast.ps1` 검사다. 실제 창, 입력, 캡처와 외부 번역 서비스는 명시적으로 요청한 검증에서만 사용한다.
- 실행 중인 앱은 알림 영역 아이콘의 **종료**로 닫는다. 창의 X는 트레이 숨김이다.
- 전체 Rebuild, Desktop, Publish, 설치 및 Git 동작은 일상 DEV-RUN에 연결하지 않는다.
- Git에는 변경한 경로만 검토해 등록한다. 커밋의 Author와 Committer에 GitHub `noreply` 주소를 사용한다.
- [개발 환경](docs/development.md) · [검사 정책](docs/branch-test-policy.md) · [Git 작업](docs/git-workflow.md) · [배포 준비](docs/release.md)
