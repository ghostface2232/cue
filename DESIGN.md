# Cue 디자인 사양서

Cue의 시각 디자인·인터랙션 규칙을 정의하는 단일 기준 문서다. 새 화면·컴포넌트를 만들거나 기존 요소를 고칠 때 여기에 맞춘다.

방향성은 **Things 3의 차분한 위계·여백·만족스러운 완료감**과 **Todoist의 빠른 캡처·우선순위 큐**를 합치되, 완성도(마이크로인터랙션·테마 정합성·네이티브 Fluent 폴리시)의 기준은 Windows 네이티브 앱 수준으로 둔다. 스택은 WinUI 3 / Windows App SDK, Mica 백드롭이다.

---

## 1. 디자인 원칙

1. **하드코딩 ARGB를 쓰지 않는다.** 모든 상호작용·텍스트·상태 색은 WinUI의 알파 기반 테마 토큰(`SubtleFillColor*`, `ControlFillColor*`, `TextFillColor*`, `SystemFillColor*`, `CardStrokeColor*` 등)을 참조한다. 이 토큰들은 라이트/다크에서 자동으로 뒤집힌다. 직접 색을 둬야 한다면 브러시의 `Color`를 `{ThemeResource ...Color}`로 묶거나 `ThemeDictionaries`로 라이트/다크를 분리한다.
2. **스톡 위에 얇게 얹는다.** 컨트롤을 통째로 리템플릿하기보다 테마 리소스 키만 오버라이드한다. 커스텀 템플릿은 필요할 때만(완료 체크 등) 최소 범위로.
3. **그림자는 진짜 떠 있는 표면에만.** 플라이아웃·팝업·슬라이드오버처럼 실제 오버레이에만 elevation을 준다. 인플로우 카드·리스트 행·디테일 패널은 그림자 0, **1px stroke**로 분리한다.
4. **악센트는 절제한다.** 면을 악센트로 칠하지 않는다. 악센트는 선택 인디케이터·포커스 링·태그 점·완료 체크 정도에만 쓴다.
5. **차분한 위계.** 정보 밀도보다 읽는 순서가 먼저다. 굵기·크기·흐림(secondary/tertiary)으로 위계를 만들고, 보조 요소는 조용하게 둔다.
6. **상호작용 어휘는 작고 반복된다.** 투명 기본 → hover → press의 한 가지 레시피, 색 전환은 83ms 하나로 통일. 같은 동작은 어디서나 같게 보이고 같게 움직인다.
7. **토큰이 진실이다.** 크기·반경·색·타이밍은 `Styles/DesignTokens.xaml`에 정의된 토큰으로만 소비한다. XAML에 리터럴 수치를 흩뿌리지 않는다.

---

## 2. 시각적 품질 기준선

- 라이트·다크 **양쪽에서** 동일한 의도가 성립해야 한다. 한쪽에서만 보이는 색·대비는 결함이다.
- 면 분리는 그림자가 아니라 stroke·톤 차이로 읽혀야 한다.
- 모든 클릭 가능 요소는 hover·press·focus 상태가 보인다. 포커스 사각형을 끄면 배경/stroke로 포커스를 반드시 대체 표시한다(접근성).
- 중첩 반경은 `inner ≤ outer − padding`으로 정렬한다.
- 정렬·간격은 토큰 스케일에 맞춘다. 임의의 한 번뿐인 수치를 만들지 않는다.
- 한국어 우선. 텍스트는 의도가 즉시 들어오는 자연스러운 한국어로 쓴다(§10).

---

## 3. 디자인 토큰 (`Styles/DesignTokens.xaml`)

전역 일관성의 단일 출처. 대부분 WinUI 토큰의 별칭이라 테마가 자동으로 따라온다.

### 타이포 (글꼴 패밀리)
- `CueFontFamily` — Pretendard JP (Regular). 본문 기본.
- `CueFontFamilySemiBold` — Pretendard JP SemiBold(별도 패밀리). **굵기 위계는 FontWeight가 아니라 패밀리 전환으로** 표현한다(정적 OTF 번들이라 SemiBold가 독립 패밀리).
- `ContentControlThemeFontFamily`를 Pretendard로 덮어 템플릿 컨트롤(버튼·리스트·입력·내비) 전체에 적용. 평문 `TextBlock`은 창 루트의 `FontFamily`로 상속.

### 타입 스케일
| 토큰 | 값 | 용도 |
|---|---|---|
| 페이지 타이틀 | 28 | `CuePageTitleTextStyle` (SemiBold) |
| `CueFontSection` | 16 | 섹션/그룹 헤더 (`CueSectionHeaderTextStyle`, SemiBold) |
| `CueFontRow` | 15 | 태스크 행 제목 |
| `CueFontRowSub` | 14 | 서브태스크·카드 헤더 |
| `CueFontSecondary` | 12 | 메타데이터·보조 라벨 |

### 반경
| 토큰 | 값 | 용도 |
|---|---|---|
| `CueRadiusSmall` | 4 | 버튼·체크·서브태스크 행·작은 표면 |
| `CueRadiusRow` | 8 | 태스크 행 |
| `CueRadiusCard` | 8 | 디테일 내부 카드 |
| `CueRadiusPanel` | 12 | 디테일 패널 |
| (알약) | height/2 | 태그/우선순위/퀵애드 등 pill 전용 |

### 색 (우선순위 큐)
- `CuePriorityP1Brush` = `SystemFillColorCritical` (매우 중요)
- `CuePriorityP2Brush` = `SystemFillColorCaution` (중요)
- `CuePriorityP3Brush` = `SystemAccentColor` (보통)
- `CuePriorityP4Brush` = `TextFillColorTertiary` (사소)

### 모션
- hover/press 등 색 전환: **83ms**, ease-out.
- 디테일 패널 진입: ~280–350ms, 시그니처 스플라인 `KeySpline 0.1,0.9 0.2,1.0`.
- 완료 "팝": 오버슈트 스케일 0.6 → 1.15 → 1.0, ~280ms, 동일 스플라인.

### 페이지 패딩
- 리스트 페이지 본문 패딩 `28,20`. 카드 내부 패딩 `16`.

---

## 4. 색상 & 테마

- **상호작용:** 투명 기본 → hover `SubtleFillColorSecondary` → press `SubtleFillColorTertiary`. 디테일 패널 내부는 카드 면 위라 hover/press를 한 단계 강하게(테마별 `#14000000`/`#1F000000` 라이트, `#22FFFFFF`/`#30FFFFFF` 다크) 패널 로컬 리소스로 올린다.
- **면 색:** 페이지/디테일 패널 배경 `LayerFillColorDefault`, 카드 `CardBackgroundFillColorDefault`, 퀵애드(떠 있는 입력) `ControlFillColorDefault`.
- **입력창 톤은 테마 분리.** 라이트는 표준 컨트롤 fill을 쓰고 포커스 시에도 그 톤을 유지(기본 흰색 번짐 방지). 다크는 반투명 흰색이 카드 위에서 과하게 밝아 보이므로 **살짝 어두운 음각 well**(검정 9–14% 오버레이)로 둔다. → `DesignTokens.xaml`의 `ThemeDictionaries`가 `TextControlBackground/PointerOver/Focused`를 라이트/다크로 나눠 정의.
- **상태색:** 성공 `SystemFillColorSuccess`, 위험 `SystemFillColorCritical`. 디테일의 저장(녹)/닫기(적) 글리프가 이를 사용하며, **hover/press 시 회색 fill로 덮지 않고** 글리프 색만 유지(누를 때 opacity만 0.6으로 살짝 낮춤).
- **선택 인디케이터:** 좌측 3px 악센트 바(`AccentFillColorDefault`, radius 1.5), 전용 컬럼이라 콘텐츠를 밀지 않는다.
- **타이틀바 캡션 버튼:** AppWindow가 그리므로 XAML 테마가 닿지 않는다. 테마별 글리프 색·hover/press 배경을 코드에서 지정하고 `ActualThemeChanged`마다 재적용한다.

---

## 5. 반경 · 그림자 · 스트로크 · 포커스

- **반경:** §3 스케일만 사용(4 / 8 / 12 + 알약). 임의 반경 금지.
- **그림자:** 인플로우 표면(카드·행·디테일 패널)은 그림자 0. 분리는 1px `CardStrokeColorDefault`(카드) / `DividerStrokeColorDefault`(내부 구분선)가 담당. stroke 카드에는 `BackgroundSizing="InnerBorderEdge"`로 1px를 반경 안쪽에 깐다. 그림자는 플라이아웃·팝업 등 진짜 오버레이에만.
- **떠 있는 느낌이 필요할 때**(퀵애드)는 그림자 대신 `CircleElevationBorderBrush`(상단 밝고 하단 어두운 그라디언트 stroke)로 미묘한 입체감을 준다.
- **포커스:** 시스템 포커스 비주얼을 기본으로. 텍스트 입력의 포커스 보더 두께는 1px로 평탄화(`TextControlBorderThemeThickness(Focused)=1`)하되 색을 악센트로 바꿔 표시. 선택형 행에서 포커스 사각형을 억제할 경우 배경/stroke로 포커스를 반드시 대체 표시한다.

---

## 6. 타이포그래피

- 위계 램프: **28(페이지) → 16(섹션) → 15(행 제목) → 14(서브) → 12(메타)**. 모두 §3 토큰/스타일로 소비.
- 굵기는 패밀리로: 제목·헤더는 `CueFontFamilySemiBold`, 본문·메타는 `CueFontFamily`.
- 색 위계: 주요 텍스트 `TextFillColorPrimary`, 메타 `TextFillColorSecondary`, 가장 조용한 라벨(그룹 헤더 등) `TextFillColorTertiary`.
- 텍스트 스타일은 중앙 스타일(`CuePageTitleTextStyle` / `CueSectionHeaderTextStyle` / `CueCardHeaderTextStyle` / `MetadataTextStyle`)로 재사용하고 인라인 폰트 리터럴을 만들지 않는다.

---

## 7. 모션 / 마이크로인터랙션

- **색 전환은 83ms ease-out 하나로 통일.** 태스크 행 배경은 `BrushTransition`(코드비하인드 스왑이 아니라 선언적 전환)으로 hover.
- **완료 인터랙션(축하 모먼트):** 빈 원(outline) → 채운 악센트 원 + 체크 글리프, 스케일 오버슈트 0.6→1.15→1.0(~280ms, 스플라인 `0.1,0.9 0.2,1.0`). 완료된 행은 opacity 0.48로 흐려지며 그 자리에 남는다(§9 참고).
- **디테일 패널 진입:** Composition `Translation`(28→0) + `Opacity`로 슬라이드 인(~280–350ms, 시그니처 스플라인).
- **리스트 재배치:** 행에 `RepositionThemeTransition`. 가상화 충돌을 피하려 모션은 **realized 컨테이너에만** 부착(ElementPrepared/Clearing)하고, Storyboard를 가상 아이템에 키하지 않는다. 드래그 리오더 표면은 기본 트랜지션을 끈 자체 모션을 쓴다.
- 횡단 규칙: 가상화 행은 Composition implicit를 Storyboard보다 우선. `ConnectedAnimation`은 쓰지 않는다.

---

## 8. 레이아웃

### 셸 (`MainWindow.xaml`)
- 상단 `TitleBar`(48) + 좌측 `NavigationView`(스톡, 얇게 오버라이드) + 콘텐츠 `Frame`. Mica 백드롭.
- 내비 폰트는 `CueFontFamily`. 평면 내비(리스트 간 뒤로가기 히스토리 없음).

### 리스트 페이지 (`TaskListPage.xaml`)
- 행 구성: 페이지 타이틀 + 캡션 → (에러 InfoBar) → 퀵애드 → 리스트(+디테일 패널).
- 본문은 좌측 리스트(가변) + 우측 디테일 패널(고정 460px) 2열. 디테일이 닫히면 리스트가 폭을 차지.
- 리스트는 두 형태: **평면 리스트**(`ItemsRepeater`, 오늘 저녁 같은 보조 섹션 포함)와 **그룹 리스트**(`ListView`, 그룹 헤더 + 행). 그룹 리스트는 프로젝트(섹션별)와 중요도(P1–P4) 뷰가 공유한다(`IsGroupedList`).

### 디테일 패널
- radius 12, 그림자 없음, 1px `CardStrokeColorDefault`, `InnerBorderEdge`, 슬라이드 인.
- 내부는 카드(radius 8, 1px stroke, 그림자 없음)들의 세로 스택. 카드: 작업 정보(메모·중요도·그룹) / 시작일+마감일 / 태그 / 체크리스트.

---

## 9. 컴포넌트 사양

### 태스크 행
- 컬럼: `[3px 선택 바][원형 체크][제목 … 우선순위 알약]`, 하단에 메타(일정 등) 한 줄.
- 선택 시 좌측 3px 악센트 바. 배경 hover는 83ms 전환. radius `CueRadiusRow`(8).
- 서브태스크는 부모 아래 들여쓴 중첩 리스트로 렌더. 존재 자체가 보이므로 "하위 작업 N" 같은 캡션은 두지 않는다. 서브태스크 행도 동일한 **원형 체크박스**·행 폰트·간격을 쓴다(메인 리스트와 일관).

### 완료 상태
- 완료해도 행은 사라지지 않고 **회색(opacity 0.48)으로 그 자리에 남으며 목록 하단으로 가라앉는다.** 다른 화면을 보고 와도 유지된다(활성 쿼리가 완료 항목을 포함, 완료-후순위 정렬). 단 사이드바 카운트 배지는 열린 작업만 센다.
- **부모를 완료하면 체크리스트(서브태스크) 전체가 함께 완료**된다. 부모만 완료되고 서브만 열린 채 남는 상태를 만들지 않는다(반복 작업이 다음 주기로 전진한 경우는 제외 — 작업이 계속되므로).

### 우선순위(중요도) 알약
- 행 제목 **뒤**에 텍스트 알약으로 표시(앞의 색 점 아님). 라벨은 매우 중요 / 중요 / 보통 / 사소.
- 배경은 우선순위 색의 옅은 틴트(~17% 알파), 글자는 같은 색의 진한 톤. radius 9.
- 색 매핑은 §3 우선순위 브러시. 텍스트 변환은 중앙 변환기로(`PriorityToLabel`), 틴트는 `PriorityToTint`, 진한 색은 `PriorityToBrush`.

### 원형 완료 체크박스 (`CueCircleCheckBoxStyle`)
- 20×20. 미완료 = 1.6px outline 원(`TextFillColorTertiary`, hover 시 secondary). 완료 = 악센트 fill + 흰 체크 글리프 + 오버슈트 팝(§7).
- 완료 토글 전용. 다중 선택/일반 체크에는 쓰지 않는다.

### 퀵애드 (omnibar 스타일)
- 떠 있는 알약(MinHeight 48, radius 24). 배경 `ControlFillColorDefault` + `CircleElevationBorderBrush`로 미묘한 입체감(그림자 아님). 선행 글리프·텍스트는 세로 중앙.
- hover 시 한 단계 밝아지고, 포커스 시 악센트 링. 텍스트/아이콘 세로 중앙 정렬.
- 입력의 자연어 일시는 기본적으로 **마감일**로 들어간다(시작일은 상세에서 명시적으로 추가).

### 시작일 · 마감일 카드
- 시작일과 마감일은 **한 카드**. 시작일을 추가하면 마감일 **위쪽**에 붙고(시작→마감의 자연스러운 순서) 구분선으로 나뉜다. 추가 전에는 그 자리에 "+ 시작일 추가" 버튼.
- 시작일은 마감일을 넘을 수 없다(달력 `MaxDate` 캡 + 마감일을 당기면 시작일도 따라 당겨짐).
- 각 날짜는 같은 줄에 날짜+시각, "종일" 체크 시 시각 숨김(밤 23:59로 만료).

### 태그
- 디테일 태그 카드는 **체크박스 없는 리스트**. 행을 누르면 우측에 체크 표시로 선택을 확인한다(색 글리프 + 이름 + 우측 체크).
- 맨 위에 "태그 없음" 항목을 두어 무태그 상태를 명시(기본 체크). 실제 태그와 상호 배타이며 저장 시 태그 id로 기록되지 않는다.

### 입력 필드
- 하단 악센트 바 없이 평탄한 1px 박스. §4의 테마 분리 톤(다크는 음각 well). 포커스 시 보더만 악센트로.

### 사이드바 / 내비게이션
- 스톡 `NavigationView` + 얇은 오버라이드. 선택 텍스트는 악센트가 아니라 `TextFillColorPrimary`로 평탄화(차분). 선택감은 fill + 좌측 악센트 알약(스톡 제공).
- 고정 항목: 모든 할 일 · 오늘 할 일 · 앞으로 할 일 · 언제든 할 일 · 나중에 할 일 · 완료한 일 · 중요도. 그 아래 **그룹**(프로젝트)·**태그**(라벨) 섹션.
- 그룹/태그 헤더는 12 SemiBold `TextFillColorTertiary`(조용한 위계).
- 항목 끝에 열린 작업 수 `InfoBadge`(절제).
- **글리프 클릭 = 즉시 선택 팝업**: 프로젝트 글리프를 누르면 아이콘 선택, 태그 글리프를 누르면 색 선택 팝업이 뎁스 없이 바로 뜬다(우클릭 컨텍스트 메뉴는 폴백).
- **사이드바 우클릭 = 표시/숨김 메뉴**: 고정 항목(오늘/앞으로/언제든/나중에/완료/중요도)을 켜고 끄는 체크 리스트(이름 왼쪽, 악센트 체크 오른쪽). 선택은 앱 로컬 설정에 저장. "모든 할 일"은 항상 표시.

### 선택 팝업 (아이콘 / 색)
- 이름 없이 아이콘/색만, 4열 그리드 플라이아웃. 나비 항목에 앵커.
- **현재 선택 항목은 링으로 강조**(아이콘은 악센트 링, 색은 고대비 링).
- 색 스와치는 hover 시 테마 fill로 덮이지 않고 **살짝 밝아지기만** 한다(흰색 쪽 블렌딩). 링은 hover/press 중에도 유지.
- 프로젝트 아이콘 세트는 사이드바 고정 글리프와 겹치지 않게 고른다. 별은 외곽선 글리프를 쓴다(나머지 아웃라인 아이콘과 톤 일치).

### 다이얼로그 / 인라인 버튼
- 인라인 보조 액션(이름 변경/삭제/+추가)은 투명 배경 + subtle hover + secondary 텍스트의 한 스타일(`CueSubtleTextButtonStyle`). 맥락당 진짜 primary는 하나만 강조.
- 공통 아이콘 버튼은 `CueIconButtonStyle`(투명 기본, 의미색은 글리프 색으로만).

---

## 10. UX 라이팅

- 한국어 우선. 의도가 즉시 들어오는 자연스러운 표현을 쓴다.
- 도메인 용어 매핑: 프로젝트→**그룹**, 라벨→**태그**, 우선순위→**중요도**(P1–P4 = 매우 중요/중요/보통/사소), 서브태스크→**체크리스트**, 마감→**마감일**, 예정→**시작일**, 상위 작업→**할 일**.
- 시간 뷰: 오늘 할 일 / 앞으로 할 일 / 언제든 할 일 / 나중에 할 일 / 완료한 일.
- 군더더기 라벨은 없앤다(예: 자명한 카드 제목은 생략).

---

## 11. 신규 요소 추가 시 체크리스트

1. **토큰부터.** 색·반경·폰트·타이밍을 새로 만들기 전에 §3 토큰에 맞는 것이 있는지 본다. 없으면 토큰을 추가하고 그걸 소비한다(리터럴 금지).
2. **테마 양쪽.** 라이트·다크에서 의도가 성립하는지 확인. 직접 색은 `ThemeResource` Color 또는 `ThemeDictionaries`로.
3. **분리는 stroke로.** 그림자를 쓰려면 그게 진짜 오버레이인지 자문한다. 인플로우면 1px stroke + `InnerBorderEdge`.
4. **상호작용 어휘 재사용.** hover/press는 §4 레시피, 색 전환은 83ms. 새 모션은 §3 타이밍·스플라인을 따른다.
5. **악센트 절제.** 면을 악센트로 칠하지 않는다. 강조는 작은 인디케이터로.
6. **위계.** 굵기(패밀리)·크기·흐림으로 읽는 순서를 만든다. 보조 요소는 조용하게.
7. **포커스·접근성.** 클릭 가능 요소는 hover/press/focus가 보이게. 포커스 사각형을 끄면 대체 표시.
8. **반경 정렬.** 중첩 시 `inner ≤ outer − padding`.
9. **카피.** §10 용어·톤에 맞춘 자연스러운 한국어.
10. **검증.** 빌드 + 테스트 통과, 가능하면 라이트/다크 실제 화면 확인.
