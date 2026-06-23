# Cue 디자인 레퍼런스 — Files 앱 분석

이 문서는 `References/Files-main` (Windows 네이티브 파일 관리자, AGENTS.md가 명시한 Cue의 **완성도/마이크로인터랙션 품질 기준**)을 깊이 분석해, Cue에 반영할 수 있는 UI/UX·레이아웃·토큰·모션 패턴을 정리한 **연구 노트**다.

- 목적: "무엇을, 어디에, 어느 수준으로" 반영할지 판단하기 위한 근거 모음. **아직 코드에 반영하지 않는다.**
- 모든 수치/키/색상은 Files 소스에서 실제 추출했고 `파일:줄` 근거를 붙였다. 경로는 `References/Files-main/src/...` 기준.
- 각 항목에 **Cue 적용 제안**과 **노력/리스크(Low/Med/High)**를 달았다. 마지막 §9에 우선순위 로드맵과 "도입하지 말 것" 목록이 있다.
- Cue의 디자인 방향은 Things 3(차분한 위계·여백·만족스러운 완료 인터랙션) + Todoist(빠른 캡처·우선순위 큐)이며, 품질 바는 Files다. 아래 제안은 **차분함을 깨지 않는 선**에서 Files의 네이티브 Fluent 폴리시를 흡수하는 것을 목표로 한다.

---

## 0. Cue 현재 상태 스냅샷

| 영역 | 현재 구현 | 핵심 한계 |
|---|---|---|
| 셸 | `MainWindow.xaml`: TitleBar(48) + 스톡 `NavigationView`(Cue/Today/Upcoming/Anytime/Someday/Logbook + Projects/Labels 그룹) + `Frame`. Mica 백드롭. | 좌측 내비가 완전 스톡. 커스텀 아이템 템플릿/선택 인디케이터/그룹 헤더 스타일 없음. |
| 리스트 | `TaskListPage.xaml`: 페이지 패딩 `28,20`. `ItemsRepeater` 태스크 행(Border, hover 코드비하인드 브러시 스왑), 서브태스크 중첩 repeater, 프로젝트 모드 `ListView` 섹션 그룹. | 행 내부에 자체 `Border`를 칠함(컨테이너 스타일이 아님). hover/press 애니메이션 없음. |
| 디테일 | 460px `Border` 카드(radius 16, `ThemeShadow`, `Translation 0,0,16`), 내부 카드 radius 12 + ThemeShadow. save/close = `HyperlinkButton` 38×38(녹색/빨강). | 패널이 `Visibility` 토글만(진입 애니메이션 없음). 인플로우 카드에 그림자 남용. |
| 색상 | 하드코딩 ARGB: hover `#0F000000`, pressed `#18000000`, 디테일카드 `#FFFEFEFE`, save `#168A45`, close `#C83C3C`. | **다크 모드에서 깨짐**(검정 틴트 hover는 어두운 배경에서 안 보이고, `#FFFEFEFE`는 흰 카드가 흰 배경 위에 묻힘). |
| 모션 | 커스텀 `ReorderSurface`(드래그 리오더, Storyboard+CubicEase). 완료는 opacity 0.48로 즉시 변경. | 완료/hover/패널에 트랜지션 없음. |
| 반경/모션 토큰 | radius 7/9/12/16 임의값. 표준 토큰/스케일 없음. | 일관성 없음. |

---

## 1. 가장 큰 메타 교훈 (Files가 일관되게 지키는 것)

분석을 관통하는 패턴 — 개별 위젯보다 이게 더 중요하다.

1. **하드코딩 ARGB를 거의 쓰지 않는다.** 상호작용 상태/텍스트/상태색은 전부 WinUI의 알파 기반 토큰(`SubtleFillColor*`, `ControlFillColor*`, `TextFillColor*`, `SystemFillColor*`)을 참조한다. 이 토큰들은 라이트/다크가 자동으로 뒤집힌다. → Cue의 4개 하드코딩 색이 다크에서 깨지는 문제의 정답.
2. **스톡 위에 얇게 얹는다.** 커스텀 컨트롤조차 `*.ThemeResources.xaml`로 `ThemeDictionaries(Light/Dark/HighContrast)`를 깔고, 의미 기반 네임스페이스 토큰(`App.Theme.Sidebar.BackgroundBrush` 등)으로 소비한다. (`App.xaml:67-124`)
3. **그림자는 "진짜 떠 있는 표면"에만.** 전체 코드에서 `ThemeShadow`는 단 3곳(Omnibar 팝업, 셸 루트, 오버레이 사이드바). 인플로우 카드·리스트 행에는 그림자 0. 분리는 **1px stroke**가 담당. TabView 그림자는 아예 끔(`App.xaml:21`).
4. **반경은 타이트한 2-스텝(4 inner / 8 container) + 알약(pill).** 커스텀 radius 스케일도, `ControlCornerRadius`/`OverlayCornerRadius` 오버라이드도 없다. 스톡 4/8을 그대로 쓴다.
5. **상호작용 vocab는 작고 반복된다.** 투명 기본 → hover `SubtleFillColorSecondary` → press `SubtleFillColorTertiary`(아이콘 opacity 0.75). 색 전환은 **83ms `BrushTransition`** 하나로 통일.
6. **악센트는 절제.** 행 전체를 악센트로 칠하지 않는다. 선택은 중립 fill + 좌측 3×16 악센트 알약. 악센트는 선택 인디케이터·태그 점·포커스 정도에만.
7. **타이포는 중앙화 + SemiBold 기준.** `TextBlockStyles.xaml`에서 Body14/Caption12/Subtitle20 스톡 토큰을 재사용하되 base를 SemiBold로.

---

## 2. 색상 / 브러시 토큰

### Files가 하는 것
- 앱 레벨 의미 토큰을 `ThemeDictionaries`로 정의: `App.Theme.Sidebar.BackgroundBrush`, `App.Theme.InfoPane.BackgroundBrush`, `App.Theme.FileArea.BackgroundBrush` 등. (`App.xaml:67-124`) 대부분 값이 WinUI 토큰의 별칭이라 테마 전환 자동.
- 선택/hover/press(사이드바, `SidebarStyles.xaml:305-368`): hover=`SubtleFillColorSecondaryBrush`, press=`SubtleFillColorTertiaryBrush`, selected=`ControlFillColorDefaultBrush`+`ButtonBorderBrush`, 선택 인디케이터=`AccentFillColorDefaultBrush`(3×16, radius 2, opacity 0→1).
- 상태색은 **항상** `SystemFillColorSuccess/Critical/CautionBrush` (`GitHubLoginDialog.xaml:91`, `FilesystemOperationDialog.xaml:192` 등). 커스텀 빨강/녹색 하드코딩 없음.
- 패널 배경은 `LayerOnMicaBaseAltFillColorDefault`("Mica 위에 얹힌 패널"의 표준색)을 사이드바·주소창·선택 탭에 공통 사용.
- 런타임 테마 커스터마이즈를 위해 배경 토큰을 `Application.Current.Resources`에 다시 써넣는 서비스(`AppResourcesService.cs:32-74`)가 있어 토큰을 별도로 둔 이유가 된다.

### Cue 적용 제안 (surface 매핑)
| Cue surface | 현재 | 제안 (Files식) | 효과 |
|---|---|---|---|
| 태스크 행 hover | `#0F000000` | `{ThemeResource SubtleFillColorSecondaryBrush}` | 다크 모드 가시성 수정. **최고 가성비.** |
| 태스크 행 press | `#18000000` | `{ThemeResource SubtleFillColorTertiaryBrush}` | 동일 |
| 태스크 행 selected | (없음/임시) | `ControlFillColorDefaultBrush` + 좌측 3px `AccentFillColorDefaultBrush` 인디케이터 | 네이티브 선택감 |
| 디테일 카드 | `#FFFEFEFE` | per-theme `Cue.DetailCard.BackgroundBrush`: L `#FFFFFFFF` / D `#12FFFFFF` (Files `App.Theme.CardBackgroundFillColorTertiaryBrush` 패턴, `App.xaml:85/103/121`) | 다크에서 흰-on-흰 수정 |
| 좌측 내비 배경 | 스톡 Mica | `Cue.Sidebar.BackgroundBrush` = `LayerOnMicaBaseAltFillColorDefault` | Mica 위 패널감 |
| 퀵애드 배경 | 스톡 | `LayerOnMicaBaseAltFillColorDefault`, 포커스 시 `TextControlBackgroundFocused` | Files 주소창과 동일 결 |
| 메타데이터 텍스트 | `TextFillColorSecondaryBrush` (유지) | 유지 + 가장 흐린 라벨엔 `TextFillColorTertiaryBrush` | 이미 정답 |
| save 녹색 / close 빨강 | `#168A45` / `#C83C3C` | `SystemFillColorSuccessBrush` / `SystemFillColorCriticalBrush` (브랜드 유지 원하면 per-theme Color 토큰으로 L 진하게/D 밝게) | 다크 대비 보장 |

**제안 Cue 토큰 세트** (App.xaml `ThemeDictionaries` 한 블록, Files `App.xaml:67-124` 미러):
```
Cue.Sidebar.BackgroundBrush     → LayerOnMicaBaseAltFillColorDefault (L/D), Transparent (HC)
Cue.DetailCard.BackgroundBrush  → #FFFFFFFF (L) / #12FFFFFF (D) / SystemColorWindowColor (HC)
Cue.QuickAdd.BackgroundBrush    → LayerOnMicaBaseAltFillColorDefault (L/D), Transparent (HC)
Cue.Row.HoverBrush              → SubtleFillColorSecondaryBrush
Cue.Row.PressedBrush            → SubtleFillColorTertiaryBrush
Cue.Row.SelectedBrush           → ControlFillColorDefaultBrush
Cue.Row.SelectionIndicatorBrush → AccentFillColorDefaultBrush
Cue.SuccessBrush                → SystemFillColorSuccessBrush (또는 브랜드)
Cue.DangerBrush                 → SystemFillColorCriticalBrush (또는 브랜드)
```

**노력/리스크:** hover/press/상태색 스왑 = **Low/Low** (즉시 가치). 디테일카드 per-theme = Low/Low. 사이드바·퀵애드 배경 = Med/Med(Mica와 상호작용, 양 테마 확인 필요). 풀 `App.Theme.*` + 런타임 오버라이드 서비스 = High/Med(사용자 테마 커스터마이즈를 원할 때만).

근거: `App.xaml`, `Styles/PathIcons.xaml`, `ThemedIcon/ThemedIcon.ThemeResources.xaml`, `Sidebar/SidebarStyles.xaml`, `Services/App/AppResourcesService.cs`.

---

## 3. 반경 / 그림자 / 스트로크 / 포커스

### Files가 하는 것
- **반경 스케일 없음 → 스톡 4/8 사용.** `ControlCornerRadius`=4(버튼·리스트/메뉴 아이템·내부 카드), `OverlayCornerRadius`=8(플라이아웃·다이얼로그·팝업). 컨테이너 카드/패널/셸/툴바는 리터럴 `8`. 12/16/19/24는 **알약·원·Omnibar** 같은 형태 전용. (`ModernShellPage.xaml:41`, `InfoPane.xaml:142/165`, `StatusCenter.xaml:82`)
- **그림자 극도로 절제.** `ThemeShadow` 3곳뿐. 유일한 명시 Z는 Omnibar 팝업 `Translation 0,0,32`(`Omnibar.xaml:93-96`). 셸 루트·오버레이 사이드바는 Z 없이 기본 elevation. 도킹 카드·행은 그림자 0. `TabViewShadowDepth=0`(`App.xaml:21`). `AttachedShadow`/`DropShadow`/`BlurRadius` 정의 없음.
- **분리는 1px stroke가 담당.** 컨테이너 카드=`CardStrokeColorDefaultBrush` 1px, 내부 구분=`DividerStrokeColorDefaultBrush` 1px, 도킹 엣지=`SurfaceStrokeColorDefaultBrush` 방향성 두께. 모든 stroke 카드에 `BackgroundSizing="InnerBorderEdge"`(1px가 radius 안쪽에 깔끔히). 텍스트 입력 포커스만 1px→2px 악센트.
- **포커스는 시스템 비주얼.** 커스텀 포커스 비주얼 없음. 툴바/탭 버튼은 `FocusVisualMargin="-3"`(링 안쪽), 리스트 행은 `FocusVisualPrimaryThickness/SecondaryThickness="0"`로 링 억제(선택은 배경/stroke로 표시).

### Cue 적용 제안
**제안 반경 스케일(App.xaml 토큰화):**
| 토큰 | 값 | Cue surface (현재→제안) |
|---|---|---|
| `CueRadiusSmall` | 4 | 버튼·체크·서브태스크 행 (7→4) |
| `CueRadiusRow` | 6–8 | 태스크 행 (9→6~8) |
| `CueRadiusCard` | 8 | 내부 카드 (12→8) |
| `CueRadiusPanel` | 8(또는 12) | 디테일 패널 (16→8, 살짝 부드럽게 원하면 12) |
| `CueRadiusPill` | height/2 | 태그/상태 알약만 |

- **그림자 = Cue의 가장 큰 비표준 지점.** 디테일 패널·내부 카드의 `ThemeShadow`+`Translation` **제거**하고 1px `CardStrokeColorDefaultBrush`로만 분리(Files `InfoPane.xaml`이 정확히 그 방식). `ThemeShadow`는 플라이아웃/팝업/슬라이드오버 패널 등 **진짜 오버레이**에만, Omnibar식 `Translation 0,0,32`.
- 모든 stroke 둥근 카드에 `BackgroundSizing="InnerBorderEdge"` 확인.
- 카드 내 버튼/행에 `FocusVisualMargin="-3"`, 선택형 태스크 행은 포커스 사각형 억제(단, 접근성 위해 배경/stroke로 포커스 표시 보장).

**노력/리스크:** 토큰 정의/반경 재조정 Low/Low(중첩 반경 정렬만 확인: inner ≤ outer − padding). 인플로우 그림자 제거 Low/Low~Med(시각 변화 — 더 평평한 게 의도). 포커스 사각형 억제 Low/Med(대체 포커스 표시 필수).

근거: `App.xaml`, `Views/Shells/ModernShellPage.xaml`, `UserControls/Pane/InfoPane.xaml`, `Controls/Omnibar/Omnibar.xaml`.

---

## 4. 리스트 행 / 타이포 / 간격 / 선택

### Files가 하는 것
- **행은 고정 높이**(콘텐츠 패딩 아님). Details 기본 36px, List 기본 32px (`LayoutSizeKindHelper.cs:59-162`). 세로 중앙정렬 + 좌우 패딩만(`Padding 12,0,...`), 세로 인셋 ≈0.
- **스타일 표면은 컨테이너(`ListViewItem`)**, 아이템 템플릿 Grid는 radius·좌우 패딩·`Margin 0,2` 거터만 추가. (Cue는 행 안에 자체 Border를 칠함 — 분기점)
- **타이포 중앙화**(`TextBlockStyles.xaml`): base를 **SemiBold 14**로 두고 body/메타는 Normal로 내림. 파일명(주요 텍스트)=암묵 기본 14 Normal, 메타=`ColumnContentTextBlock`(Caption 12 + `Opacity 0.6`, ellipsis), 그룹 헤더=`SubtitleTextBlockStyle`를 **16 SemiBold로 오버라이드**(`DetailsLayoutPage.xaml:1415`) + 옆에 흐린 카운트(14, Spacing 4).
- **선택/hover = 중립 fill**(스톡 `ListViewItemBackgroundSelected/PointerOver`), 악센트 아님. 행 radius=`ControlCornerRadius`(4). 포커스 사각형 억제. 마퀴 선택만 악센트 사각형.
- **체크박스 reveal-on-hover**: 선택 체크박스는 썸네일 위에 `Opacity 0`로 겹쳐두고 pointer-over 시 0→1(아이콘과 교체). Files는 다중선택용이라 평소 숨김.
- **빈 상태**: 일러스트 없는 단일 `TextBlock`(`FolderEmptyIndicator.xaml`). → Cue의 "표시할 할 일이 없습니다." 접근을 검증.

### 주요 메트릭
| 항목 | 값 | 근거 |
|---|---|---|
| Details 행 높이 | 28/36/40/44/48 (기본 36) | `LayoutSizeKindHelper.cs:59-76` |
| List 행 높이 | 24/32/36/40/44 (기본 32) | `:121-138` |
| 행 좌우 패딩 | `12,0,...` / 메타 `10,0,...` | `DetailsLayoutPage.xaml:990,1025` |
| 행 거터 | `Margin 0,2` | `ColumnLayoutPage.xaml:214` |
| 행 radius | `ControlCornerRadius`=4 | `GridLayoutPage.xaml:282` |
| 그룹 헤더 | 16 SemiBold + 카운트(Spacing 4), `Margin 0,0,0,4` | `DetailsLayoutPage.xaml:1406-1438` |
| 태그 알약 | H24, `Padding 8,0`, radius 12 | `DetailsLayoutPage.xaml:1112-1120` |

### Cue 적용 제안 (surface 매핑)
| # | 제안 | 변경 | Cue surface | 노력/리스크 |
|---|---|---|---|---|
| A | 타입 스케일 토큰화 | `CueTitleFontSize=15`, `CueSecondaryFontSize=12`, `CueSectionFontSize=16` 리소스화, 인라인 리터럴 제거 | 행 제목/서브태스크/메타/섹션 | Low/Low |
| B | 메타=12 + 흐림 유지 | `TextFillColorSecondaryBrush` 유지, 단일 스타일로 통일 | 메타데이터 | Low/Low |
| C | 행 패딩을 좌우 가중으로 | 태스크 `10,10`→`12,8`, 서브태스크 `10,7`→`12,6`, 세로 중앙정렬 | 행 | Low/Low |
| D | 반경 완화 | 행 9→6, 서브태스크 7→4 | 행 Border | Low/Low |
| E | 선택=중립 fill, 악센트는 우선순위 큐에만 | 스톡 `ListViewItem...` 브러시 또는 미러, 악센트는 좌측 우선순위 바/점 | 선택/hover | **Med** (자체 Border면 컨테이너로 재구성 필요) |
| F | 체크박스는 **상시 표시**(투두 핵심), reveal는 **보조 액션**에만 | hover 시 우측 액션 클러스터(수정/날짜/삭제) opacity 0→1 | 행 hover | Med/Low |
| G | 섹션 헤더 20→16 SemiBold + 흐린 카운트 | "오늘 저녁" 포함 | 섹션 헤더 | Low~Med/Low |
| H | 페이지 헤더 28 유지 → 28/16/15/12 4-스텝 램프 | 리터럴 중복 제거 | 헤더 | Low/Low |
| I | 빈 상태 — 미니멀 유지 + 약간 폴리시 | 중앙정렬, secondary, 14, top margin ~80-120 | 빈 상태 | Low/Low |

**Cue↔Files 분기 요약:** ① 반경 9/7 vs 4 → 6/4 ② 선택이 자체 Border vs 시스템 중립 브러시 ③ 패딩 대칭 `10,10` vs 좌우 가중 고정높이 ④ 섹션 헤더 20 vs 16 ⑤ 인라인 리터럴 vs 중앙 토큰 ⑥ 체크박스: Files는 숨김/reveal, **Cue는 상시 표시 유지**(보조 액션만 reveal 차용).

근거: `Styles/TextBlockStyles.xaml`, `Helpers/Layout/LayoutSizeKindHelper.cs`, `Views/Layouts/DetailsLayoutPage.xaml`(+cs), `ColumnLayoutPage.xaml`, `GridLayoutPage.xaml`, `UserControls/FolderEmptyIndicator.xaml`.

---

## 5. 사이드바 / 내비게이션

### Files가 하는 것
- 메인 사이드바는 **스톡 NavigationView가 아님** — `ItemsRepeater` 기반 커스텀 `SidebarView`+`SidebarItem`(`Files.App.Controls`). 단, **Settings 내비는 스톡 NavigationView를 얇게 리스타일**(`Styles/NavigationViewItemButtonStyle.xaml`) → Cue(스톡)에 더 적합한 모델.
- 선택 인디케이터: 좌측 `Rectangle` **3×16, radius 2, `AccentFillColorDefaultBrush`, opacity 0→1**, 전용 3px 컬럼(콘텐츠 안 밀림). (`SidebarStyles.xaml:111-123,307-313`)
- 섹션 헤더(=Cue의 Projects/Labels): 별도 컨트롤 없이 확장 상태 아이템. **FontSize 12, SemiBold, `TextFillColorTertiaryBrush`**(흐림) — 일반 아이템보다 조용함(차분한 위계). (`SidebarStyles.xaml:254-272`)
- hover=`SubtleFillColorSecondaryBrush`, press=`SubtleFillColorTertiaryBrush`, 배경 전환 83ms `BrushTransition`. 셰브론 `E76C` 12×12/FontSize10/`TextFillColorSecondaryBrush`, 0→90° 회전, 자체 pointer 영역(행 선택 없이 토글).
- 메트릭: 펼침 pane 300, compact 56; 행 높이 32, item radius `ControlCornerRadius`(4), 아이콘 16×16, **들여쓰기 16px×깊이**(`SidebarItem.Properties.cs:60`), 섹션 갭 top 12. 스톡 리스타일은 item MinHeight 36, 선택 텍스트를 **`TextFillColorPrimaryBrush`로 평탄화**(악센트 텍스트 제거).
- **카운트 배지 없음**(스톡 리스타일은 `InfoBadge` 슬롯 지원). 드래그 리오더는 커스텀에 내장(2px 악센트 인서트 라인).

### Cue 적용 제안
**Path A — 스톡 NavigationView 얇은 오버라이드 (권장, Low):** Files `NavigationViewItemButtonStyle.xaml`처럼 **테마 리소스 키만** 오버라이드하는 `ResourceDictionary` 추가.
1. 선택 텍스트 평탄화: `NavigationViewItemForegroundSelected*` → `TextFillColorPrimaryBrush`(악센트 텍스트 제거 → 차분). 선택감은 fill+악센트 알약으로.
2. fill 완화: selected=`ControlFillColorDefaultBrush`, hover=`...Secondary`, press=`...Tertiary`.
3. item 높이: `NavigationViewItemOnLeftMinHeight` 36(또는 더 조밀하게 32).
4. 선택 알약: 스톡이 이미 3×16/radius2/악센트 → 변경 불필요.
5. **그룹 헤더(Projects/Labels) 조용하게: FontSize 12, SemiBold, `TextFillColorTertiaryBrush`** — "차분한 위계"의 최고 가성비 변경.
6. compact/flyout-on-children는 스톡이 무료 제공.

**Path B — 커스텀 아이템 템플릿 (Med/High, 나중):** Project→Section→Task **16px/레벨 들여쓰기 트리**, 셰브론 정렬, 드래그 리오더가 필요해지면 `SidebarItem`(ItemsRepeater + flat-with-depth) 포팅. 스톡 NavigationView의 ~2레벨 한계가 블로커가 될 때.

**카운트 배지:** 원하면 스톡 `NavigationViewItem.InfoBadge`(숫자/secondary 색, 절제). Low~Med.

**노력/리스크:** 선택 텍스트 평탄화/fill 완화/그룹 헤더 = 전부 **Low/Low**. InfoBadge = Low~Med(VM 카운트 배선). 트리 들여쓰기/리오더 = High/Med(커스텀 필요).

근거: `Sidebar/SidebarStyles.xaml`, `Sidebar/SidebarView.xaml`, `Sidebar/SidebarItem.Properties.cs`, `Sidebar/FlatSidebarItem.cs`, `Styles/NavigationViewItemButtonStyle.xaml`.

---

## 6. 툴바 / 버튼 / 커맨드 레이아웃

### Files가 하는 것
- 커스텀 `Toolbar`+`ToolbarButton` 패밀리(아이콘 우선, 기본 투명, 오버플로 인지)와 스톡 `CommandBar` 병용. 주소창은 Omnibar+BreadcrumbBar 조합.
- 아이콘 버튼 티어: Small 32×32 / Medium 40×32 / Large 40×40, **패딩 4, radius `ControlCornerRadius`(4), 아이콘 16**, 아이템 간격 4, 구분자 1px `DividerStrokeColorDefault`(`4,4,4,4` 마진, RadiusX/Y 0.5). (`ToolbarButton.ThemeResources.xaml`, `ToolbarSeparator.xaml`)
- **재사용 가능한 hover/press 레시피:** 기본 투명 → hover `SubtleFillColorSecondary`+1px `ControlStrokeColorDefault` → press `SubtleFillColorTertiary`(전경 secondary, 아이콘 opacity 0.75) → toggle checked는 악센트 전경+2px 악센트 보더(solid 악센트 fill 아님).
- Omnibar = 모드 멀티플렉싱 알약(38 높이, radius 19): Path(=BreadcrumbBar 표시)/Command/Search 전환, 포커스 시 편집 TextBox+제안 팝업, 인라인 clear 버튼 36×28, 보더 1px→2px 악센트.

### Cue 적용 제안
| 항목 | 제안 | Cue surface | 노력/리스크 | 권장 |
|---|---|---|---|---|
| 공통 아이콘 버튼 스타일 | `CueIconButtonStyle`(32×32, 아이콘16, 패딩4, radius4, subtle-fill 상태). `HyperlinkButton`→`Button`. 의미색은 **전경/글리프 색**으로만 유지(중립 hover/press fill 위에 녹/적). | 디테일 save/close, 모든 아이콘 액션 | Low/Low | **먼저 도입** |
| 인라인 텍스트 버튼 통일 | 투명 배경+subtle hover+secondary 텍스트의 "tertiary 액션" 스타일 1개, 맥락당 진짜 primary 1개만 강조 | 섹션추가/이름변경/삭제/+새 라벨/추가 | Low/Low | 도입 |
| 헤더 액션 행(경량) | 카드/보더 없는 `StackPanel Horizontal Spacing 4`의 아이콘 버튼(정렬/필터/더보기), 우측 정렬. 오버플로 엔진은 생략 | 리스트/프로젝트 헤더 | Med/Low | 선택적 |
| 구분자 토큰 | 1px `Rectangle`, `DividerStrokeColorDefaultBrush`, `4,4,4,4`, RadiusX/Y 0.5 | 액션 그룹 분리 | Low/Low | 필요 시 |
| 퀵애드 알약 스타일 | 알약(radius≈height/2), `ControlFillColorDefault`, 1px→2px 악센트 on focus, 텍스트 있을 때 인라인 clear(36×28) | 상단 퀵애드 TextBox | Low/Low | **스타일만 도입** |

근거: `Toolbar/ToolbarButton/ToolbarButton.xaml`(+ThemeResources), `Toolbar/ToolbarSeparator/ToolbarSeparator.xaml`, `Controls/Omnibar/Omnibar.xaml`(TextBox 템플릿), `UserControls/NavigationToolbar.xaml`.

---

## 7. 마이크로인터랙션 / 애니메이션

### Files의 모션 어휘 (실측)
- **타이밍 상수:** **83ms**(`0:0:0.083`) = 모든 hover/press 배경 `BrushTransition`의 표준(여러 파일 반복). 100ms = 셰브론 회전. 167ms = 페이드. 200ms = CubicEase 정착. 240ms = 탭 리오더. **275ms** = 오버스크롤 스케일 "팝". **350ms** = pane open/close, 그리드 리오더.
- **이징:** `CubicEase`, 그리고 **시그니처 KeySpline `0.1,0.9 0.2,1.0`**(빠른 시작·부드러운 착지) = 모든 사이드바 pane 전환(`SidebarView.xaml:210-329`). 표준 ease-out cubic `1-(1-t)³`(`StorageControlsHelpers.cs:257`).
- **오버슈트 "팝":** `NavigationInteractionTracker.cs:191-194` Scale 키프레임 `0.5→1.3x, 1.0→1.0x` 275ms = "만족스러운" 핵심 패턴. 스프링 복귀는 `DampingRatio=1`(바운스 없음).
- **컴포지션 우선:** 가상화 행 관련 모션은 Storyboard보다 implicit/Composition(컴포지터 스레드, 리사이클 안전). 리스트는 `AddDeleteThemeTransition`/`RepositionThemeTransition`, 자체 드래그 영역은 기본 트랜지션을 **끔**. `ConnectedAnimation`은 안 씀.
- **ThemedIcon:** SVG path 기반 아이콘이 Outline/Filled/Layered + 색 역할을 **VisualState(`useTransitions:true`)**로 전환. **toggle 시 Outline→Filled+악센트** 자동 — Cue의 미완료→완료(빈 원→채운 악센트 원) 전환에 직결.

### Cue 적용 제안 (우선순위)
| 우선 | 인터랙션 | Cue surface | API | 타이밍/이징 | 노력/리스크 |
|---|---|---|---|---|---|
| P0 | hover 배경 페이드 | 태스크 행 | `Grid.BackgroundTransition`=`BrushTransition` (코드비하인드 스왑 제거, VisualState로) | 83ms | Low/Low |
| P0 | press 스케일 | 태스크 행 | Composition `Scale` Vector3KF, CenterPoint=중앙 | ~100ms, 0.98x, ease-out | Med/Low |
| P0 | **완료 인터랙션** | 행 체크 + opacity | ThemedIcon식 Outline→Filled 크로스페이드 + 오버슈트 팝 | 팝 0.5→1.2→1.0 ~250-275ms / dim 1.0→0.48 ~167ms | Med/Low |
| P1 | 디테일 패널 진입/퇴장 | 디테일 pane | Composition `Translation`+`Opacity` (또는 Spline Storyboard) | 350ms, KeySpline `0.1,0.9 0.2,1.0` | Med/Low |
| P1 | 리스트 add/remove/reposition | 태스크 `ItemsRepeater` | `ElementCompositionPreview.SetImplicitShow/HideAnimation` + implicit `Offset` (ElementPrepared에서 부착, ElementClearing에서 해제) | ~200-250ms add, ~250-350ms reposition, ease-out | Med/**Med** (가상화 충돌 주의) |
| P2 | 내비 선택 전환 | 사이드바 | `BrushTransition`(83ms) + 공유 인디케이터 `Offset` 슬라이드 | 83ms bg / 150-200ms 슬라이드 | Low~Med/Low |

**횡단 권장:** 모든 색 전환 = **83ms ease-out cubic** 통일. pane 슬라이드 = **KeySpline `0.1,0.9 0.2,1.0`**. 축하 모먼트(완료) = **오버슈트 ×1.2 후 ×1.0 ~275ms**. 가상화 행은 **Composition implicit > Storyboard**. `ConnectedAnimation` 불필요. (Cue의 기존 `ReorderSurface`는 Storyboard 기반 — 추후 implicit Offset으로 옮기면 더 매끄럽고 저비용.)

근거: `Helpers/Navigation/NavigationInteractionTracker.cs`, `Data/Behaviors/StickyHeaderBehavior.cs`, `Controls/ThemedIcon/ThemedIcon.cs`(+xaml), `Sidebar/SidebarView.xaml`, `Styles/NavigationViewItemButtonStyle.xaml`, `Views/Layouts/DetailsLayoutPage.xaml`, `Styles/TabBarStyles.xaml`, `Controls/Storage/StorageControlsHelpers.cs`.

---

## 8. 통합 디자인 토큰 제안 (요약)

App.xaml에 한 번 정의해 전역 일관성 확보. 대부분 WinUI 토큰 별칭(테마 자동) + 소수 브랜드/스케일 값.

```
// 색 (ThemeDictionaries)
Cue.Row.HoverBrush / PressedBrush / SelectedBrush / SelectionIndicatorBrush
Cue.Sidebar.BackgroundBrush / Cue.QuickAdd.BackgroundBrush / Cue.DetailCard.BackgroundBrush
Cue.SuccessBrush / Cue.DangerBrush

// 반경
CueRadiusSmall=4  CueRadiusRow=6~8  CueRadiusCard=8  CueRadiusPanel=8(/12)  CueRadiusPill=height/2

// 타이포
CueTitleFontSize=15  CueSecondaryFontSize=12  CueSectionFontSize=16   (페이지 28 → 4스텝 램프)

// 모션
CueDurationHover=83ms  CueDurationPanel=350ms  CueDurationPop=275ms  CueDurationDim=167ms
CueEaseStandard=ease-out cubic   CueSplinePanel=0.1,0.9 0.2,1.0
```

---

## 9. 우선순위 로드맵 & 도입하지 말 것

### 도입 우선순위
**P0 — 토큰화 + 다크 모드 정합성 (Low 비용, 즉시 가치)**
1. 색 4종 하드코딩 → 테마 토큰(hover/press/디테일카드/save·close). 다크 모드 깨짐 수정.
2. 반경 스케일(4/6/8) 토큰화 및 7/9/12/16 정리.
3. 타입 스케일(15/12/16, 페이지28) 토큰화.
4. 인플로우 카드 `ThemeShadow`+`Translation` 제거, 1px stroke + `InnerBorderEdge`로 분리.
5. hover를 코드비하인드 스왑 → 83ms `BrushTransition`.

**P1 — 차분한 위계 + 핵심 모션 (Low~Med)**
6. 내비 스톡 오버라이드: 선택 텍스트 평탄화, fill 완화, Projects/Labels 헤더 12 SemiBold tertiary.
7. 공통 `CueIconButtonStyle` + 인라인 텍스트 버튼 통일(디테일 패널부터).
8. 완료 인터랙션(빈 원→채운 악센트 원 + 오버슈트 팝 + opacity dim).
9. 디테일 패널 진입/퇴장(350ms, 시그니처 스플라인).
10. 섹션 헤더 16 SemiBold + 흐린 카운트, 빈 상태 폴리시.

**P2 — 선택적 깊이 (Med~High, 필요 시)**
11. 리스트 add/remove/reposition implicit 애니메이션(가상화 주의).
12. 행 선택을 컨테이너 중립 fill + 좌측 우선순위 큐로 재구성, hover 보조 액션 reveal.
13. 퀵애드 알약 스타일 + 인라인 clear.
14. 내비 InfoBadge 카운트.
15. (먼 미래) 커스텀 `SidebarItem` 트리(16px/레벨, 드래그 리오더), 기존 `ReorderSurface`를 Composition implicit로 이전.

### 도입하지 말 것 (Cue 범위 초과 / 과설계)
- `CommandBar` + `IsDynamicOverflowEnabled` 오버플로 엔진, `ToolbarSplitButton`/`ToolbarFlyoutButton`(Files에서도 XAML 빈 스텁, 전부 코드비하인드), 68px `AppBarButton` 리템플릿(7개 오버플로 상태).
- 풀 **Omnibar**(모드 멀티플렉싱 + 제안 팝업 + 브레드크럼) — 투두 앱에 과함. 알약 스타일만 차용.
- **BreadcrumbBar** — Cue엔 경로 위계 없음(NavigationView가 내비 담당).
- `ConnectedAnimation` — 불필요.
- 카운트 배지 남용 — Files도 사이드바엔 안 씀. 쓰더라도 절제.
- 런타임 테마 커스터마이즈 서비스(`AppResourcesService`) — 사용자 테마 편집을 정식 기능으로 넣을 때만.

### 검증 메모
- 모든 색/배경 변경은 **라이트+다크 양쪽**에서 확인(특히 Mica 위 `LayerOnMicaBaseAlt` 과틴트 주의).
- 가상화 모션은 **realized 컨테이너에만** 부착(ElementPrepared/Clearing), Storyboard를 가상 아이템에 키하지 않기.
- 포커스 사각형 억제 시 **대체 포커스 표시(배경/stroke)** 보장(접근성).
- 중첩 반경은 inner ≤ outer − padding 정렬 확인.

---

## 핵심 참조 파일 (Files-main/src)
- 색/토큰: `Files.App/App.xaml`, `Files.App.Controls/ThemedIcon/ThemedIcon.ThemeResources.xaml`, `Files.App/Services/App/AppResourcesService.cs`
- 반경/그림자/stroke: `Files.App/Views/Shells/ModernShellPage.xaml`, `Files.App/UserControls/Pane/InfoPane.xaml`, `Files.App.Controls/Omnibar/Omnibar.xaml`
- 리스트/타이포: `Files.App/Styles/TextBlockStyles.xaml`, `Files.App/Helpers/Layout/LayoutSizeKindHelper.cs`, `Files.App/Views/Layouts/DetailsLayoutPage.xaml`, `UserControls/FolderEmptyIndicator.xaml`
- 사이드바: `Files.App.Controls/Sidebar/SidebarStyles.xaml`, `SidebarView.xaml`, `Files.App/Styles/NavigationViewItemButtonStyle.xaml`
- 툴바/버튼: `Files.App.Controls/Toolbar/ToolbarButton/ToolbarButton.xaml`(+ThemeResources), `Toolbar/ToolbarSeparator/ToolbarSeparator.xaml`
- 모션: `Files.App/Helpers/Navigation/NavigationInteractionTracker.cs`, `Files.App.Controls/ThemedIcon/ThemedIcon.cs`, `Files.App.Controls/Sidebar/SidebarView.xaml`

> 상태: **연구·분석 완료. 코드 미반영.** 다음 단계로 P0부터 단계적 적용을 검토한다(각 단계는 AGENTS.md의 "한 번에 하나씩, 단계별 검증·커밋" 원칙을 따른다).
