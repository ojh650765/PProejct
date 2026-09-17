# 레벨 제작 템플릿

현재 제공하는 것은 **완만한 지형의 도로 레벨** 템플릿입니다. 물·동굴의 형상과 본편으로 들어가는 연결은 별도로 설계합니다.

1. `meadow.json`을 복사하고 새 `scene`, 지형 크기, 도로 폭, NPC·아이템·소품 위치를 작성합니다. `at`은 X/Z이며 높이는 지형에서 자동 계산됩니다.
2. `python Tools/Level/template_level.py Tools/Level/templates/meadow.json`으로 검사합니다. 도로를 막는 소품, 겹치는 배치, 지형 밖 배치와 과도한 높낮이는 거절합니다.
3. 같은 명령에 `--write`를 붙이면 `Assets/Game/Data/Levels/template_<scene>.json`이 생성됩니다.
4. Unity에서 생성된 JSON을 선택하고 **Tools → Poké Lab → Level → Build Selected Template**을 실행합니다. 공통 플레이어·카메라·서비스 구성을 복사하고 지형 메시, 소품, NPC, 아이템, 출구와 NavMesh를 생성합니다. 플레이어 시작점에서 모든 상호작용 지점까지의 실제 길찾기도 검사합니다.
5. 스토리는 기존 `Tools/Story/new_episode.py`로 연결합니다. 새 NPC의 고유한 `actor` 이름을 사용하고, 일반 대화에는 `--shot`을 지정하지 않습니다. `python Tools/Story/validate_story.py`로 참조를 검사합니다.
6. 생성된 초안은 자동으로 본편이나 빌드 목록에 추가되지 않습니다. 완성된 레벨의 양방향 출구/도착 마커를 연결하고 빌드 목록에 추가합니다. 이름이 같은 템플릿은 재생성되므로, 레벨 수정은 생성된 씬 대신 원본 JSON에 기록합니다.

예시의 귀환 출구는 Town의 `Spawn_FromRoute202`로 연결됩니다. 레벨을 다른 위치에 붙일 때는 목적지에 있는 정확한 마커 이름으로 바꿉니다. 외부로 이어지는 도로라면 양쪽 경계 높이도 맞춰야 합니다. 물을 통과하는 길은 이 도로 템플릿으로 자동 생성하지 않습니다.

검사 결과: `Temp/template_level_verification.json`. 예시 재생성: **Tools → Poké Lab → Level → Verify Meadow Template**.
