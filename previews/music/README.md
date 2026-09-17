# BGM 재생 소유권

`MusicDirector` 하나가 테마곡·조우 음악·포획 음악·승리 음악을 담당합니다.
동시에 음악 소스를 두 개 재생하지 않고, 현재 곡을 내린 뒤 다음 곡을 올립니다.
새 곡이나 장면 전환은 이전 비동기 로드, 재생 예약, 팡파르 뒤의 복귀 예약을 취소합니다.

기존에는 36초짜리 승리 음악이 별도의 채널에 남아, 필드 음악이 돌아온 뒤에도
같이 재생될 수 있었습니다. 이제 필드·메뉴가 음악을 다시 맡으면 승리 음악도 교체됩니다.
효과음·UI·루프 API로 들어온 Music 버스 요청 역시 중앙 재생기로 보냅니다.

가산 로딩된 씬의 임시 오디오 컴포넌트가 기존 소유자를 제거하지 않도록,
AV 호스트는 이미 등록된 소유자를 우선합니다. 오디오 컴포넌트를 끄거나 제거하면
그 컴포넌트가 만든 재생 소스도 정리됩니다.

검사 메뉴: Tools > Poké Lab > Verification > Play Test Music Ownership

메뉴 → 마을/필드/202번도로 → 실내 → 메뉴 로딩과 빠른 곡 변경, 연출 우선권,
조우 → 전투 → 승리 → 필드, 재생 대기 중 취소, 중복 컨트롤러를 검사합니다.
검사는 Unity에서 실제 AudioSource의 재생 여부와 Music 믹서 채널을 관찰합니다.
온라인 계정이나 사용자 저장 파일을 수정하지 않습니다.

- [검사 결과](verification.json)

최종 결과: 25개 검사 통과, 실패 0. 전체 전환 중 동시 BGM 재생 소스는 최대 1개였습니다.


2026-09-18: Imported 13 DP recordings from the user-provided local soundtrack folder. Retired procedural music generation and its eight WAV files. Re-ran all 25 music playback checks successfully; maximum concurrent music sources: 1. See `verification.json`.
