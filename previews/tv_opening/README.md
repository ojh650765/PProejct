# 주인공 집 TV 시작 장면

새 게임은 주인공 집의 TV 앞에서 시작합니다. 붉은 갸라도스가 나온 호수 방송을
본 뒤 엄마와 대화하고 밖으로 나갑니다. 방송 이미지와 같은 4:3 CRT 화면을
Blender MCP로 만들었고, 화면에는 조명에 씻겨 나가지 않는 전용 재질을 적용했습니다.

- [방송 장면](004.png)
- [플레이 검사](verification.json)
- [Blender 메시 미리보기](../television_blender.png)
- [방송 이미지 원본](../../Assets/Game/Art/Environment/Interior/Textures/TV_Broadcast.png)
- [재생성 스크립트](../../Tools/Blender/environment/build_television.py)

대화창은 어두운 모자이크 배경을 사용합니다. 방송을 보는 동안 카메라는 고정하며,
주인공 집의 벽 조명은 화면과 글자의 대비를 해치지 않도록 낮췄습니다.
