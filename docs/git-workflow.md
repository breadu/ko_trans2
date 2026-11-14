# Git 작업

- `main`을 안정 브랜치로 유지한다. 변경은 `main`에서 만든 작업 브랜치에 등록하고 테스트 후 검토한다.
- 전체 작업 폴더 대신 변경한 경로만 명시적으로 stage한다. 커밋 전에 `git diff --cached`와 `git var GIT_AUTHOR_IDENT`, `git var GIT_COMMITTER_IDENT`를 확인한다.
- GitHub 설정에 표시된 공식 `noreply` 주소를 사용한다. 설정, 키, 로그, 게임 이미지, 모델, 빌드 출력은 로컬에 보관한다.
- 빌드와 테스트 스크립트는 자동으로 커밋·푸시하거나 `main`을 변경하지 않는다.
