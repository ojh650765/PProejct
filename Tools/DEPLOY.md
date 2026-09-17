# GitHub Pages 배포

프로젝트 루트의 `DeployGame.exe`를 더블클릭합니다.
이 프로젝트를 Unity에서 열고 Play 모드를 종료한 상태여야 합니다. Unity MCP는 localhost:8080에서 연결합니다.
Python(requests, websocket-client), Git, Chrome, GitHub push 인증은 현재 PC의 설치를 사용합니다.

실행 순서: 현재 Unity 프로젝트 확인 → WebGL 빌드 → 로컬 브라우저 로딩/오류 검사 → gh-pages 커밋·업로드 → 실제 사이트 로딩 검사.
실패하면 이후 단계는 실행하지 않습니다. 강제 push나 다른 브라우저 프로세스 종료는 하지 않습니다.
사이트: https://ojh650765.github.io/PProejct/

- `DeployGame.exe --dry-run`: 빌드와 로컬 검사만 실행합니다.
- `DeployGame.exe --deploy-existing`: 이미 빌드된 결과를 검사 후 게시합니다.
- `DeployGame.exe --help --no-pause`: 창 대기 없이 사용법을 출력합니다.

실행 파일 소스는 `Tools/DeployLauncher.cs`, 파이프라인은 `Tools/release_game.py`입니다.
Unity 빌드 결과는 `Temp/release_build.json`, 로컬/온라인 캡처는 `Temp/deploy_gate_*.png`에 남습니다.
소스 코드 커밋은 별도로 관리하며, 실행 파일은 gh-pages의 빌드 결과만 커밋합니다.
로그인·매칭·PP 서버는 `Server/pokelab-online`의 별도 Cloudflare Worker입니다. 이 실행 파일은 게임 웹사이트를 배포합니다.
