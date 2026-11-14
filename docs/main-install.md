# 로컬 설치본

`Run.cmd`는 저장소의 `release/KoTrans2.exe`를 실행한다. [릴리즈 준비 절차](release.md)에 따라 검증한 자체 포함 Windows x64 폴더를 준비한다.

```powershell
pwsh -NoProfile -File scripts/Install-Main.ps1 -PackageDirectory '<검증한 폴더>' -SourceCommit '<소스 커밋 SHA>'
```

앱이 실행 중이면 알림 영역 아이콘에서 **종료**를 선택한다. 스크립트는 기존 앱 설치본을 백업한 뒤 교체한다. 사용자 설정은 `%LOCALAPPDATA%/KoTrans2`에서 관리한다.
