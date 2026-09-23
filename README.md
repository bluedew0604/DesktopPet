# DesktopPet

화면 오른쪽 아래 한구석에서 자기 생활을 하는 작은 데스크톱 펫 (Windows, Unity 6).

- 쉬기 · 주변보기 · 기지개 · 걷기 · 졸기 · 잠자기
- 생활 장면: 과자 옮겨 먹기 · 방석 정리 · 종이공 놀이
- 클릭하면 반응, 드래그하면 자리째 이동
- 메뉴 (캐릭터 우클릭 또는 트레이 아이콘 클릭): 보통/조용히 모드, 숨기기, 오늘 일기, 제자리로, 종료
- 하루 세 줄 일기 (실제로 끝까지 한 일만 기록, 최근 30일 보관)
- 사용자 정보 수집 없음 (현재 시각, 클릭·드래그만 사용)

## 열기 / 빌드

1. Unity **6000.6** (Built-in Render Pipeline)로 프로젝트 폴더를 연다.
2. 메뉴 **DesktopPet > 1. 프로젝트 자동 설정** (처음 한 번)
3. 메뉴 **DesktopPet > 2. 빌드하기** → `Build/DesktopPet.exe`

에디터 Play 모드에서는 투명 창 없이 동작만 확인할 수 있다.
키: `1` 과자 · `2` 방석 · `3` 공 · `Z` 졸기 · `S` 잠 · `W` 걷기 · `Q` 조용히 · `H` 숨김 · `D` 일기 · `R` 그림 다시 불러오기

## 그림 넣기

`Assets/StreamingAssets/Art/` 에 정해진 이름의 PNG만 넣으면 된다. 자세한 목록은
[`Assets/StreamingAssets/Art/그림_넣는_법.txt`](Assets/StreamingAssets/Art/그림_넣는_법.txt) 참고.
그림이 없으면 코드로 그린 임시 캐릭터가 나온다.

## 구조

| 폴더 | 내용 |
|---|---|
| `Assets/DesktopPet/Core` | 행동 판단 엔진 (Unity 비의존 순수 C#) |
| `Assets/DesktopPet/Runtime` | 투명 창 · 입력 · 트레이 · 화면 표시 · 저장 |
| `Assets/DesktopPet/Editor` | 자동 설정 · 빌드 메뉴 |
