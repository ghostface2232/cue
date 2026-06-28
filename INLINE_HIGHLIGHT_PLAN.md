# 빠른 입력창 인라인 액센트 강조 — 구현 계획서

## 1. 목적과 동작 목표

Cue의 핵심 차별점은 한국어 자연어 파서다. 지금 이 파서는 **커밋(Enter) 시점에만** 동작해
사용자는 결과(제목·날짜·반복으로 분해된 모습)를 *입력이 끝난 뒤에야* 본다. 이 계획은 파서를
**입력 단계로 끌어올려**, 빠른 입력창에서 날짜·시각·반복으로 인식된 글자를 **그 자리에서
(in-place)** 액센트 컬러로 강조한다. 파서가 제목에서 무엇을 떼어 어디로 가져갔는지를
사용자 눈앞에 즉시 드러내, 핵심 기능을 UX로 가시화하고, 오인식(제목으로 의도한 입력이
날짜로 빨려나가는 false positive)을 **커밋 전에** 인지·교정하게 한다.

지향점은 Discord의 `@`멘션·Notion의 `/`커맨드 같은 자연스러운 인라인 강조이되, **트리거
문자 없이 자연어 파싱만으로** 작동하는 것이다.

### 동작 목표 (Acceptance)

1. 날짜/시각/반복으로 해석된 토막에만, 정확히 그 자리에 액센트가 들어간다.
2. 날짜·시각·반복은 **시각적으로 구분되는** 강조를 갖는다(예: `오늘`·`15:00`·`매주`가
   서로 다른 톤). 색만으로는 전달하지 않고 보조 신호(밑줄/굵기)를 병행한다.
3. 한글 IME 조합 중인 미완성 글자에는 **절대** 서식이 들어가지 않는다(글자 깨짐 없음).
4. 내용을 일부 지우거나 고치면 액센트가 깔끔히 취소되고, **다른 토막으로 전이되지 않는다.**
5. `금요일` → `금요일마다`로 고치면 일회성 날짜에서 **매주 반복**으로 파싱·강조가 매끄럽게
   갱신된다.
6. 토큰을 클릭하면 대안(날짜/시간/반복)으로 한 동작에 교정하거나 **원문으로 되돌릴** 수 있고,
   되돌린 단어는 이후 타이핑·커밋에서 **다시 날짜로 빨려가지 않는다.**
7. 어떤 IME·입력 거동에서도 "할 일을 못 넣는" 상태가 되지 않는다(평문 폴백 보장).

---

## 2. 현재 구조 (착수 전 사실 확인)

이 계획은 추상이 아니라 현재 코드 위에서 검증한 사실에 선다.

| 항목 | 현재 상태 | 위치 |
|---|---|---|
| 입력 컨트롤 | `TextBox`, `Text` 양방향 바인딩(`UpdateSourceTrigger=PropertyChanged`) | `Pages/TaskListPage.xaml:443` |
| placeholder | 두 `Run` 오버레이(`QuickAddPlaceholderName` 액센트 + `Suffix`), `QuickAddIsEmpty`로 가시성 | `Pages/TaskListPage.xaml:486~500` |
| 메트릭 | `Padding="46,13,14,9"`, `CornerRadius="24"`, 선두 검색 `FontIcon` | `Pages/TaskListPage.xaml:445~485` |
| Enter 커밋 | `QuickAdd_KeyDown` → `AddCommand` | `Pages/TaskListPage.xaml.cs:180` |
| 파싱 시점 | **커밋 시점에만** `_parser.Parse(text, …)` | `ViewModels/TaskListViewModel.cs:260` |
| VM 계약 | `QuickAddText`(TwoWay) / `QuickAddIsEmpty` / `OnQuickAddTextChanged` / placeholder 프로퍼티 / `AddCommand` | `ViewModels/TaskListViewModel.cs:119~134, 253~293` |
| 파서 엔진 | 룰 파이프라인, 각 룰이 매치 구간을 **물리적으로 제거** | `Cue.Parsing/KoreanDateParser.cs:99~125` |
| 룰 셋 | 7개 built-in + boost seam | `Cue.Parsing/BuiltInRules.cs` |
| 회귀 테스트 | 표 기반 대규모 고정 | `Cue.Tests/KoreanDateParserTests.cs`, `PARSING.md` |
| 프레임워크 | WinUI 3 / Windows App SDK (`net10.0-windows10.0.26100`, min `10.0.17763`) | — |
| 테마 토큰 | 액센트·하이컨트라스트 딕셔너리 | `Styles/DesignTokens.xaml` |

### 2.1 엔진이 원문 오프셋 충실도를 파괴하는 메커니즘 (이 계획의 가장 큰 토대)

`KoreanDateParser.Apply()`(`KoreanDateParser.cs:99~125`)는 룰이 매치할 때마다 다음을 한다:

```csharp
work = work.Remove(match.Index, match.Length).Insert(match.Index, " ");
```

그 결과:

1. 룰 N의 `Match.Index`는 **원문 인덱스가 아니라 변형된 `work`의 인덱스**다.
2. 제거 길이와 무관하게 항상 `" "` 한 칸을 삽입 → 길이가 줄며 인덱스 드리프트.
3. 한 룰 내부에서도 변형된 텍스트에 재매치(guard 64회), 마지막에 `CollapseWhitespace`
   (`:173`)로 공백을 접는다. 라이브러리 폴백도 변형된 `work`에 `IndexOf`(`:157`).

→ **원문 기준 char range를 내보내는 일은 단순한 필드 추가가 아니라 엔진 리팩터다.** 인라인
강조·되돌리기·클릭 교정이 전부 이 토대 위에 선다.

### 2.2 이미 존재하는 자산

- `금요일`(일회성) vs `금요일마다`(매주) **분기는 이미 파서에 있다.** `RecurrenceQuickAddRule`
  (`BuiltInRules.cs:78~96`)은 `매주`/`마다` 마커가 없는 bare 요일에 `false`를 반환해 one-off
  `WhenDateRule`로 넘긴다. 목표 5의 *로직*은 이미 있으므로, 필요한 것은 "수정 시 전체 재파싱
  + 재틴트"뿐이다.
- 룰 패턴은 **named group**(`Korean.cs`의 `rel`/`wd`/`mon`/`domd`/`h`/`min`/`half`/`daypart`
  등 + `BuiltInRules.cs` `RecurrenceQuickAddRule`의 `weekly_wd`/`rwd`/`daily`/`monthly_dom`/
  `mada` 등)으로 짜여 있어 group별 `Index/Length`를 준다 → kind별(날짜/시각/반복) 하위
  토큰 분해가 가능하다.
- `Cue.Parsing`은 UI/스토리지 의존이 없는 **순수 라이브러리**(AGENTS.md §105)이고 `Cue.Tests`로
  고정된다 → 계약 확장 + 단위 테스트의 적소. 파서는 **never-throw** 불변식을 가진다.
- `Microsoft.Recognizers`의 한국어 DateTime 모델은 현재 **no-op**(AGENTS.md §106) — 폴백은
  지금 토큰을 만들지 않아도 기능 저하가 아니다.

### 2.3 캡슐화 압력

`TaskListPage.xaml.cs`/`TaskListViewModel.cs`는 이미 크다. RichEditBox·IME·재틴트·팝오버
복잡도를 여기에 흘리면 god-object 압력이 커진다. → 모든 신규 로직은 처음부터 **전용
UserControl 경계** 안에서 짓는다(8절).

---

## 3. 아키텍처 원칙 (전 단계 공통)

- **단일 진실은 "현재 전체 재파싱 결과"다.** 인라인 틴트는 *뷰 전용 투영*이며, 증분으로
  쌓지 않는다. 매 발화마다 전체 default로 리셋한 뒤 토큰 span만 다시 칠한다. "잔류 서식이
  존재할 상태" 자체를 없애는 것이 목표 4(전이 없음)의 보장이다.
- **틴트는 항상 현재 파스의 투영이다 — 의미적 번짐 주의.** 파서는 전체 줄·단일 When 슬롯·
  고정 룰 순서다. 따라서 한 토막을 고치면 재파싱이 *먼 토막의 분류*까지 바꿀 수 있다. 이는
  버그가 아니라 파서의 본질이며, "틴트 = 현재 전체 재파싱의 투영"으로 정의하면 일관되게
  처리된다. 목표 5(`금요일`→`금요일마다`)도 같은 원리로 자동 동작한다.
- **억제(override)는 에디터가 들고 있는 상태다.** 파서는 stateless라 되돌린 단어를 다음
  발화에서 다시 빨아간다. 되돌리기의 지속성은 에디터의 per-span override가 보장한다(6절).
- **never-throw 유지.** 위치 계산 실패는 throw가 아니라 "틴트 없음"으로 degrade한다. 인라인
  강조가 깨져도 할 일 입력 자체는 항상 가능해야 한다(킬 스위치, 9절).

---

## 단계 0 — IME 조합 신호 스파이크 (착수 전 필수 게이트, 버리는 코드)

**왜 먼저인가:** 이 계획 전체의 트리거 설계는 "지금 조합 중인가"를 알 수 있다는 가정 위에
선다(단계 2). WinUI의 `RichEditBox`는 자체 edit context를 내부 관리하므로 이 신호가 공짜가
아니다. 신호가 없으면 게이트 설계 전체가 성립하지 않으므로 **다른 코드를 짓기 전에** 증명한다.

**검증 항목**

- 최소 `RichEditBox` 하나에 `TextCompositionStarted` / `TextCompositionChanged` /
  `TextCompositionEnded` 핸들러를 달고, Windows 기본 한글 IME 입력 시 **실제로 발화하는지**
  확인. 발화한다면 "조합 시작~종료" 구간을 `bool _isComposing` 플래그로 잡을 수 있는지.
- 매 commit마다 전체 구간을 재서식할 때 **caret 위치**가 튀는지, **Ctrl+Z(native undo)**
  스택이 깨지는지, 조합 중(`ㄱ→가→갈`) 글자에 서식이 새는지 직접 관찰.

**분기 판정**

- ✅ 신호 확보 + 재서식 깔끔 → 단계 3의 "full reset + re-tint" 단일 진실 경로로 진행.
- ⚠️ caret/undo 훼손 → **부분 재서식**(바뀐 토큰 span만 갱신)으로 전환. 이 결정을 단계 3
  착수 전에 확정.
- ❌ 조합 신호 자체가 없음 → 대안 조사: `CoreTextEditContext` 직접 사용, 또는 조합 중 들어오는
  중간 글자(자모 단위 변화)를 `TextChanged`에서 식별. 확보 불가 시 단계 2 트리거를 재설계한
  뒤에야 진행.

**산출물:** "이 신호로 조합/비조합을 가른다 + 어느 재서식 경로로 간다"는 결론 1줄 + 동작하는
스파이크 스니펫. **코드는 버린다.**

---

## 단계 1 — 파서 계약 확장 & 원문 char range 토대 (엔진 리팩터)

**왜 먼저인가:** 인라인·되돌리기·클릭 교정이 전부 이 계약 위에 선다. 검증의 무게를 *테스트
가능한 파서*로 밀어, 수동 검증 영역을 IME·서식으로 좁힌다. **UI 변경 없음.**

### 1.1 토큰 계약 (additive — 기존 소비자 무영향)

`ParsedQuickAdd`(`ParsedQuickAdd.cs:16`)의 기존 필드(`Title`/`When`/`Recurrence`/
`WhenAssigned`/`WhenHasTime`)는 **그대로 둔다**(`TaskListViewModel.cs:260~280`의 기존 커밋
경로가 깨지지 않도록). 토큰 목록만 **추가**한다.

```csharp
public enum QuickAddTokenKind { RelativeDate, AbsoluteDate, Time, Recurrence, Someday }

public sealed record QuickAddToken(
    QuickAddTokenKind Kind,
    int Start,          // 원문(=화면 표시 텍스트) 기준 char offset
    int Length,         // 원문 기준 길이
    string SourceText,  // 되돌리기용 원문 substring
    string Interpreted  // 해석값 요약(예: "2026-06-29", "15:00", "FREQ=WEEKLY;BYDAY=FR")
);

// ParsedQuickAdd에 추가
public IReadOnlyList<QuickAddToken> Tokens { get; init; } = Array.Empty<QuickAddToken>();
```

> **불변식:** 방출되는 char range는 반드시 **"화면에 보이는 입력 문자열" 기준**이어야 한다.
> 내부 collapsed `work` 기준이면 단계 3의 재틴트와 단계 5의 클릭 역매핑이 둘 다 깨진다.

### 1.2 엔진 모델 변경 — 물리 제거 → 길이 보존 span-mask

현재의 "Remove/Insert로 물리 제거"를 버리고 다음으로 바꾼다:

- 룰이 claim한 **원문 구간**을 `claimed`(원문 길이만큼의 마스크/구간 리스트)에 기록한다.
- 각 룰에는 **claimed 구간을 같은 길이의 공백으로 가린 masked view**를 매치 입력으로 준다.
  이렇게 해야 "앞 룰이 텍스트를 소비했다"는 기존 전제(예: "오늘 저녁 7시 … 저녁 약속"에서
  앞의 `저녁`이 소비되면 제목의 `저녁`은 재인식 금지)가 보존된다.
- **길이 보존이 핵심이다.** `Insert(idx," ")`가 길이를 줄였던 것과 달리, masked view는
  claim 구간을 **같은 길이**의 공백으로 치환하므로 인덱스가 원문과 1:1로 유지된다 →
  `Match.Index` → 원문 인덱스 **역매핑 비용 0**.
- 인접 단어 융합 방지(예전의 `" "` 삽입 목적)는 **최종 `Title` 산출 시** claimed 경계에서
  공백을 넣어 처리한다. `Title` 결과 자체는 기존과 동일(원문에서 claimed 제거 +
  `CollapseWhitespace`).
- 룰이 span을 claim하는 순간(`Extract`가 true), 그 match의 원문 range와 kind를 토큰 후보로
  기록한다.

> **대안 (저위험 폴백):** span-mask 리팩터가 guard 루프·write-once·`Extract` decline 의미를
> 건드려 회귀 위험이 크다고 판단되면, 기존 strip 파이프라인을 두되 working↔원문 인덱스
> 매핑 테이블(`int[] originMap`)을 누적하는 **오프셋 매핑(additive)** 으로 대체한다. 이때
> 삽입되는 `" "` 한 칸과 최종 `CollapseWhitespace`까지 매핑에 반영해야 정확하다. 1차에서는
> `WhenDateRule` 한 룰로 PoC 후 통과 시 경로를 확정한다.

### 1.3 kind 분해 — named group 단위 range

토큰을 통짜로 칠하지 않고 날짜/시각/반복을 **구분**하려면 하위 토큰이 필요하다(예:
"다음주 금요일 저녁 7시에"는 한 매치, 조사 `에` 포함). 정규식은 group별 `Index/Length`를 준다:

- named group → `QuickAddTokenKind` **매핑표**를 정리한다(`rel`/`wd`/`mon`/`domd` → 날짜,
  `h`/`min`/`half`/`daypart` → 시각, `weekly`/`daily`/`monthly_dom`/`rwd`+`mada` → 반복 등).
- 한 매치 안에서 성공한 의미 group의 range를 각각 토큰으로 방출한다. 조사·구두점 group은
  제외하거나 인접 토큰에 흡수한다.

### 1.4 라이브러리 폴백

`LibraryFallback`(`KoreanDateParser.cs:131~171`)의 `IndexOf` 기반 소비도 **원문 기준** 토큰을
기록하도록 맞춘다. 한국어 모델이 현재 no-op이므로 지금은 토큰 미방출(range 없이 When만
채우는 현 동작 유지, 기능 저하 아님). 모델이 켜지면 동일 계약으로 흘리도록 TODO만 명시한다.

### 1.5 테스트 (검증 무게의 중심)

- **기존 회귀 전부 그대로 green** 임을 먼저 확인(엔진 변경이 깨지 않는지가 1차 관문).
- 신규 토큰 테스트: PARSING.md 코퍼스의 대표 입력에 대해 `(Kind, Start, Length, SourceText)`를
  **원문 기준**으로 단언. 경계 케이스 포함:
  - 다중 매치, 조사 흡수, `CollapseWhitespace` 후 offset, 글루 회피(`내일로`/`오늘의집`/`3월의`),
    어순 변형, 오인식 방지, 복합("다음주 금요일 저녁 7시에 보고서").
  - `금요일` vs `금요일마다`: 같은 접두에서 토큰 Kind가 `AbsoluteDate` → `Recurrence`로 바뀌고
    range가 확장되는지 단언.

**완료 기준:** 콘솔/유닛 레벨에서 원문 substring·char range·kind가 정확. UI 무변경. 기존 +
신규 테스트 전부 green.

---

## 단계 2 — RichEditBox 토대 + VM 브리지 + IME 게이트

**왜 여기서:** 부분 색칠은 단색 `TextBox`로 불가하니 컨트롤 교체가 인라인의 물리적 전제다.
가장 위험한 한글 입력 거동을 이 단계에서 끝내, 나중에 전체 인터랙션을 다시 뜯는 사태를 막는다.

### 2.1 컨트롤 교체

- `Pages/TaskListPage.xaml:443`의 퀵애드 `TextBox` → `RichEditBox`(전용 UserControl 내부,
  8절과 한 몸).
- placeholder(두 `Run` 오버레이)·선두 검색 `FontIcon`·`Padding="46,13,14,9"`·`CornerRadius="24"`·
  Pretendard 계열 폰트·테마 split 색을 RichEditBox 메트릭에 맞춰 재배치한다.

### 2.2 VM 바인딩 브리지

`RichEditBox`에는 TwoWay로 묶을 `Text` 의존 속성이 없다. `QuickAddText`(TwoWay)·
`QuickAddIsEmpty`·placeholder·`AddCommand`가 전부 `QuickAddText`를 읽으므로(§2 표) 문서↔VM
수동 동기화가 필요하다.

- 문서 변경 → `Document.GetText(TextGetOptions.NoHidden, out var s)` → VM `QuickAddText`에 push.
- 외부에서 VM이 비울 때(`AddAsync` 끝 `QuickAddText = string.Empty`, `TaskListViewModel.cs:293`;
  화면 전환) → `Document.SetText`로 pull.
- `QuickAddIsEmpty`/placeholder 가시성/`AddCommand`가 계속 `QuickAddText`를 읽도록 유지.
- Enter 커밋 핸들러 `QuickAdd_KeyDown`(`TaskListPage.xaml.cs:180`) 재배선.
- **offset 함정 가드:** `GetText`가 붙이는 말미 `\r`, BMP 1:1(한글 안전)·서로게이트(이모지)
  어긋남을 가드한다.

### 2.3 RTF 부작용 차단

- 붙여넣기 서식 주입 차단(plain text paste 강제), Enter 줄바꿈 가로채기(커밋으로), 자동
  서식/스펠체크 off.
- **테마 색:** `CharacterFormat`은 `ThemeResource`처럼 자동 반전하지 않는다. 기본 텍스트
  색과 액센트 색을 `ActualThemeChanged`에서 재적용한다.

### 2.4 IME / 조합 게이트

- 단계 0에서 확정한 신호로 `TextCompositionStarted/Changed/Ended`를 받아 `_isComposing`
  플래그를 관리한다.
- **재틴트는 비조합 상태에서만.** 조합 중엔 절대 서식을 적용하지 않는다(조합 글자 깨짐 방지).

**완료 기준(수동):** 한글 입력이 깨지지 않고, 텍스트가 VM과 정확히 동기화되며, 커밋이 종전과
동일하게 동작.

---

## 단계 3 — 발화 트리거 + 봉쇄 메커니즘 (한 몸)

서식을 켜는 순간 번짐·재진입·깜빡임·caret 이동이 발생하므로, 봉쇄를 서식 *다음에* 얹지 않고
*함께* 짠다.

### 3.1 발화(재파싱·재틴트) 트리거

```
(띄어쓰기 입력  OR  500ms 멈춤  OR  IME commit)  AND  비조합 상태
```

- 타이머(`DispatcherTimer`)가 떴어도 조합 중이면 **보류하고 commit을 기다린다.**
- 새 키 입력이 오면 대기 중 타이머를 **취소**(전이 방지의 일부).
- 비조합을 세 트리거의 **공통 전제**로 두어 조합 미완 글자에 서식이 새는 것을 원천 차단.
- 매 발화 재파싱 비용은 짧은 문자열 기준 무시 가능(룰당 최대 64회×7 + 폴백). 폴백 모델은
  생성자에서 1회만 생성되므로(`KoreanDateParser.cs:44`) 발화마다 재생성되지 않는다. 필요 시
  파싱을 UI 스레드 밖에서 수행하고 결과만 마샬링.

### 3.2 봉쇄

- **번짐/전이:** 매 발화 **전체 default로 리셋 후 토큰 span만 재색칠**. 잔류 서식이 존재할
  상태 자체를 없앤다(증분 틴트 금지). 이것이 목표 4("다른 곳으로 전이되지 않음")의 보장.
- **재진입:** 서식 적용 구간을 가드 플래그로 감싸 그 사이 `TextChanged`를 무시.
- **깜빡임:** `Document.BatchDisplayUpdates()` / `ApplyDisplayUpdates()`로 묶는다.
- **caret:** 재틴트 전 `Document.Selection.StartPosition/EndPosition` 저장, 후 복원.
- **undo 오염:** full-reset의 서식 op가 Ctrl+Z 스택에 섞이지 않도록 undo 그룹으로 격리한다.

> **kind별 색조:** 단계 1의 `QuickAddTokenKind`에 따라 날짜/시각/반복을 서로 다른 톤으로
> 칠하고, 색약·하이컨트라스트를 위해 밑줄/굵기 등 색 외 신호를 병행한다(목표 2,
> `DesignTokens.xaml`의 HighContrast 딕셔너리 반영).

**완료 기준(수동):**
- 한 글자씩 지울 때 액센트가 정확히 취소되고 인접 토큰으로 전이되지 않음.
- 한글 조합 중 서식이 들어가지 않음(조합 완료 후에만).
- 빠른 연속 입력 중 깜빡임·caret 점프 없음.
- `금요일` → `금요일마다` 수정 시(트리거 발화 → range 재방출 → 재틴트의 일반 경로로) 반복
  강조로 자동 갱신.

---

## 단계 4 — 되돌리기 = 억제(override) 상태 모델

**왜 독립 단계인가:** 파서는 stateless다. `금요일`을 제목으로 되돌려도 다음 발화에서 다시
날짜로 빨려간다. false positive를 커밋 전에 잡는 것이 이 기능의 핵심 가치이므로, 에디터가
억제 상태를 들어야 한다(목표 6).

### 4.1 상태

- 에디터가 `List<{ AnchorText, ApproxStart, Decision }>` 형태의 override를 보유.
- **앵커는 char offset이 아니라 안정 키**(원문 substring + 위치 근접)로 잡는다 — offset은
  매 발화 변하므로(전이 문제와 동형). 해당 텍스트가 사라지면 그 override도 폐기.

### 4.2 적용

- **재파싱 입력 마스킹(우선 채택):** override가 "이 span은 제목"이라 하면, 재파싱 입력에서
  그 span을 파서가 못 보게 가린다. 토큰이 안 생기므로 색칠도 안 되고, `When`/`Recurrence`
  재계산까지 자동으로 일관되게 반영된다.
- **커밋도 동일 override 적용:** 되돌린 단어가 `Title`에 남도록 `AddAsync` 경로에 반영한다
  (4.3 참고).

**완료 기준(수동):** `금요일` 되돌리기 후 계속 타이핑해도 다시 날짜로 빨려가지 않음. 되돌린
단어가 커밋 시 제목에 남음.

---

## 단계 5 — 클릭 수정 인터랙션 + 커밋 결합

**왜 여기서:** 슬래시 명령과 달리 파싱 토큰은 소비된 뒤에도 복원 가능해야 한다. 되돌리기를
팝오버의 고정석으로 두어 교정을 항상 한 동작 거리에 둔다.

### 5.1 팝오버

- `Document.GetRangeFromPoint(point, …)`로 클릭 → char index → 토큰 역매핑(단계 1의 range
  사용). 토큰 사각형은 `range.GetRect`/`GetPoint`로 구해 팝오버(Flyout/Popup) 앵커로 쓴다.
- **팝오버 내용:** kind별 대안 — 날짜(내일/모레/…), 시간 드롭다운, 반복 대안(매주/격주/…) —
  그리고 **항상 "원문으로 되돌리기"**(토큰의 `SourceText` 사용)를 고정석으로 둔다.
- 치환 후 **재파싱 → range 재계산 → caret 재배치.** 되돌리기 선택은 단계 4의 override를 등록.

### 5.2 커밋이 라이브(교정 반영) 파스를 소비

현재 커밋은 raw 텍스트를 새로 `Parse`한다(`TaskListViewModel.cs:260`). 인라인 표시용 파스와
클릭 교정 결과가 이미 있는데 Enter 때 raw를 재파싱하면 **팝오버 교정과 override가 버려진다.**

- 빠른 입력 컨트롤이 **현재 표시 중인 `ParsedQuickAdd`(교정·억제 반영본)** 를 보유한다.
- 커밋은 raw 재파싱 대신 이 라이브 파스를 소비한다(텍스트가 마지막 파스 이후 바뀌었으면
  override를 적용해 한 번 더 파스해 동기화).
- VM 진입점은 plain string Parse 대신 `ParsedQuickAdd`를 받는 **오버로드를 추가**하되, 기존
  string 경로(`AddAsync`)도 남겨 호환을 유지한다. `AddAsync`의 후처리(종일 변환, 반복 앵커
  드롭 등 `TaskListViewModel.cs:265~280`)는 그대로 재사용.

**완료 기준(수동):** 토큰 클릭 → 팝오버 → 대안/되돌리기가 텍스트·파싱·caret을 일관되게 갱신.
교정 후 Enter 시 교정 결과가 커밋에 반영됨.

---

## 단계 6 — 캡슐화 (단계 1~5의 전제, 별도 작업 아님)

위 전부를 처음부터 **전용 UserControl/behavior 경계**(가칭 `OmniInputBox`) 안에서 짓는다.

- 토큰 char range의 라이브 재계산, IME 게이트, 봉쇄, override 보유, 팝오버는 이 컨트롤의 책임.
- `TaskListViewModel`은 `QuickAddText`/`AddCommand`(+ `ParsedQuickAdd` 오버로드) 계약만 유지.
- 이미 큰 `TaskListPage.xaml.cs`로 RichEdit/IME/재틴트 복잡도가 새지 않도록 경계를 **먼저**
  세운다.

---

## 9. 횡단 요건 (모든 단계에 적용)

- **킬 스위치 / 폴백:** 인라인 강조는 가장 트래픽 높은 컨트롤이다. 특정 IME에서 깨지면
  사용자가 할 일을 못 넣는다. 평문 `TextBox`로의 graceful degradation 플래그를 둔다(목표 7).
- **접근성·하이컨트라스트:** 액센트 컬러 단독은 색약·고대비에서 무력화된다. 밑줄/굵기 등 색
  외 신호를 병행한다(목표 2). 추후 Narrator/툴팁 보강 여지를 남긴다.
- **never-throw 유지:** 파서는 이미 throw하지 않는다(`KoreanDateParser.cs:92`). 위치-계약 확장
  후에도 이 불변식을 깨지 않는다 — 위치 계산 실패는 "틴트 없음"으로 degrade.
- **PARSING.md는 회귀 코퍼스다**(AGENTS.md §112): 행 추가 → 룰/토큰 추가 → 재테스트.

---

## 10. 위험 & 검증 매트릭스

| 영역 | 위험 | 자동(CI) | 수동 |
|---|---|---|---|
| IME 조합 신호 검출 | 신호 없으면 트리거 설계 붕괴 | ❌ | ✅ 단계 0 스파이크 |
| 파서 계약·토큰 range·kind | 엔진 리팩터, 회귀 깨짐 | ✅ 단위 테스트 (단계 1) | — |
| RichEditBox ↔ VM 동기화 | TwoWay Text 상실 | 부분 | ✅ 입력/커밋 |
| 재틴트 번짐/전이/깜빡임/caret | 서식 켜는 순간 발생 | ❌ | ✅ 삭제·수정·복합 입력 |
| 되돌리기 억제 지속 | stateless 재흡수 | 부분(계약 후필터 테스트) | ✅ 입력 지속 시나리오 |
| 커밋이 라이브 파스 소비 | 교정 유실 | 부분 | ✅ 교정 후 커밋 |
| 클릭 수정 | 좌표→토큰 역매핑 | ❌ | ✅ 팝오버 상호작용 |

---

## 11. 진행 순서 요약

```
단계 0  IME 조합 신호 스파이크        ← 게이트: 신호 없으면 단계 2 트리거 재설계 (버리는 코드)
   └─ 단계 6  OmniInputBox 경계 먼저 세우기      ← 골격
        └─ 단계 1  파서 계약 + 엔진 리팩터       ← UI 무변경, 테스트로 완결 (가장 큰 리스크)
             └─ 단계 2  RichEditBox + VM 브리지 + IME 게이트
                  └─ 단계 3  발화 트리거 + 봉쇄 (단계 2와 한 몸)
                       └─ 단계 4  되돌리기 억제 모델 (핵심 가치)
                            └─ 단계 5  클릭 수정 + 커밋 결합
```

> 두 개의 load-bearing 리스크는 **단계 0(IME 신호)** 와 **단계 1(원문 char range)** 이다. 둘 다
> 스파이크/테스트로 먼저 못 박은 뒤 사용자 가시 기능(2~5)에 들어가야 일정이 보호된다.
