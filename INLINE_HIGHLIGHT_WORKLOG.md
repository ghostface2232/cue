# 빠른 입력창 인라인 강조 — 작업일지

계획서: [`INLINE_HIGHLIGHT_PLAN.md`](INLINE_HIGHLIGHT_PLAN.md). 각 단계의 **무엇을·왜**만 간결히 기록한다.

---

## 단계 0 — IME 조합 신호 스파이크 (버리는 코드) ✅
- **무엇:** `RichEditBox`에 조합 이벤트를 달아 한글 IME 조합 시작~종료 신호가 실제 발화하는지, full-reset 재서식 시 caret/undo가 깨지는지 검증.
- **결론:** **RichEditBox + 문서 `CharacterFormat`(글자색)** 경로 확정. 오버레이 칩 방식은 기각(복합 표현에서 어긋남). 스파이크 코드는 제거, 레시피만 박제.
- **왜:** 트리거 설계 전체가 "지금 조합 중인가"를 알 수 있다는 가정 위에 서기 때문. 가장 큰 미지수를 먼저 못 박음.

## 단계 1 — 파서 계약 확장 + 원문 char range 토대 ✅
- **무엇:** `ParsedQuickAdd`에 위치 토큰(`QuickAddToken{Kind,Start,Length,Text}`) 추가. 엔진의 "물리 제거"를 **길이보존 마스킹**으로 교체 → 모든 match index가 원문 기준. named group으로 날짜/시각/반복 분리.
- **왜:** 인라인·되돌리기·클릭교정이 전부 "원문 기준 정확한 char range" 위에 섬. 검증 무게를 테스트 가능한 파서로 밀어 수동 검증 영역을 축소. **UI 무변경.**

## 단계 2 — OmniInputBox(RichEditBox) + VM 브리지 + IME 게이트 ✅
- **무엇:** 퀵애드 `TextBox` → 전용 `OmniInputBox`(RichEditBox)로 교체. 문서↔VM(`QuickAddText`) 수동 양방향 브리지(루프 가드), plain-text 붙여넣기 강제, Enter=커밋(줄바꿈 차단), 조합 중 Enter는 IME 확정만.
- **왜:** 부분 색칠은 단색 TextBox로 불가 → 컨트롤 교체가 물리적 전제. 복잡도를 페이지/VM god-object로 흘리지 않도록 경계를 컨트롤 안에 둠.

## 단계 3 — 인라인 액센트 틴트 (발화 트리거 + 봉쇄) ✅
- **무엇:** 비조합 상태에서만 재틴트(조합 중 절대 서식 금지). 트리거 = 공백 | idle(500ms) | `CompositionEnded`, 버전당 1회 재파싱. 매 발화 **전체 default 리셋 후 토큰 span만 재색칠**(단일 1색), `BatchDisplayUpdates`/caret 저장·복원/재진입 가드로 봉쇄.
- **왜:** "틴트 = 현재 전체 재파싱의 투영" 단일 진실로 두면 번짐·전이가 구조적으로 사라짐.

### 단계 3 후속 — 성능 근본 수정 ✅
- **증상:** 입력 시 커서 추종이 굼뜸.
- **적대적 교차검토(서브에이전트 3):** 토큰 메모이즈 스킵 시도는 회귀 3건 유발 + *싼 비용(paint)*만 줄임 → **revert**.
- **진짜 병목:** `PreferenceDateParser`가 매 키 입력마다 `new KoreanDateParser()` → Microsoft.Recognizers 모델을 매번 빌드(**호출당 ~14ms**). 파서 인스턴스를 **설정 시그니처 기준 캐시**(재사용 시 ~0.2ms, **~70배**). 시각·동작 무변경.
- **유지:** 키 입력당 `GetText` COM 왕복 3→1 축소.

## 단계 4 — 되돌리기 = 억제(override) 상태 모델 ✅
- **4.1/4.2 (파서):** `IDateParser.Parse(..., suppressedSpans)` + `TextSpan` 추가. 엔진을 **인식 뷰**(suppressed 공백화, 길이보존) ≠ **consumed 마스크**(룰이 실제 claim한 곳)로 분리 → 되돌린 단어는 인식에서 빠지되 **제목엔 남음**. empty-suppression은 기존 경로와 증명적 동일(회귀 0).
- **4.3 (에디터, 순수):** `SuppressionTracker.Reproject` — 단일 연속 편집 델타로 억제 span 이동(앞 이동 / 뒤 유지 / 내부 수정 시 폐기). 경계 삽입은 before/after 처리(`금요일`→`금요일마다` 유지). UI 미연결.
- **왜:** 파서는 stateless → false positive를 커밋 전에 잡으려면 에디터가 억제 상태를 들어야 함. 핵심 가치(되돌린 단어가 다시 날짜로 안 빨려감)의 토대.

## 단계 5 — 클릭 수정 인터랙션 + 커밋 결합 ✅(코드)
- **5.1 억제 plumbing + 커밋 재파싱:**
  - `QuickAddSubmission(RawText, SuppressedSpans, DocumentVersion)` 신설(`Corrections`는 비텍스트 교정 도입 시까지 보류 — MVP 무근거).
  - `OmniInputBox`가 억제 상태(`_suppressed`)를 보유. 매 편집/외부 set마다 `SuppressionTracker.Reproject(_lastText→new)`로 reproject(4.3 소비자). Tokenizer에 억제 span을 넘겨 **되돌린 단어는 토큰 미방출 → 재틴트 안 됨**.
  - `Submit` 이벤트가 `QuickAddSubmission` 운반. VM `AddAsync`(Enter/테스트 경로)·`SubmitQuickAddAsync`(컨트롤 경로)가 **단일 parse-and-save 코어** 공유. 코어는 저장 직전 **현재 clock/tz로 `suppressedSpans` 적용 재파싱**(라이브 파스는 표시용 캐시) → 자정/타임존 staleness 방지.
  - raw 무trim 전달(§2.2.1): 억제 offset 정합 + 파서가 title 자체 trim. VM 회귀 테스트 2건 추가(억제 커밋 → 단어 제목 잔류·미스케줄 / 무억제 → 스케줄).
- **5.2 토큰 클릭 팝오버(생산자):**
  - 탭 → caret 위치(`Selection.StartPosition`)로 토큰 역매핑(GetRangeFromPoint 좌표계 추측 회피). RichEditBox가 Tapped를 handled 처리하므로 `AddHandler(..., handledEventsToo:true)`로 수신.
  - `MenuFlyout`: **해석 헤더**(비클릭, 아이콘+원문→해석값) + kind별 프리셋 + **고정석 "원문으로 되돌리기"**. 되돌리기 = 억제 span 등록 후 재틴트(생산자). 대안 = 텍스트 치환 → 브리지 경유 재파싱 → caret 재배치.
- **왜:** 4.3의 생산자(팝오버 되돌리기)·소비자(reproject)가 여기서 붙어 false positive를 커밋 전에 교정. 커밋 신선도는 저장 직전 재파싱이 보장.

### 단계 5 후속 — 팝오버 내용 재정리 + RichText 부작용 차단 ✅
- **서식 UI 제거:** RichEditBox `SelectionFlyout`/`ContextFlyout`을 `{x:Null}`로 꺼 선택 시 볼드/이탤릭 바와 서식 우클릭 메뉴 제거(평문 입력 의도). 우클릭 복붙은 함께 사라짐(Ctrl 단축키는 유지).
- **팝오버 내용 확정**(사용자와 합의 — 프리셋 단어 교체 + 해석 헤더 방향):
  - 헤더: 📅/🕒/🔁/History 아이콘 + `원문 → 해석`. 날짜/시각은 파스 결과를 읽어 표시(`VM.PreviewQuickAdd` → `QuickAddPreview{DateText,TimeText}`, `M월 d일 (ddd)`/`tt h:mm` ko-KR). 반복은 원문 자체가 규칙이라 화살표 생략, 언젠가는 `→ 날짜 없음`.
  - **날짜(일반: 내일·3월15일 등):** 오늘·내일·모레·이번 주말·다음 주.
  - **날짜(요일: 금요일 등):** **현재 주를 인식**해(이번/다음/다다음) ① 인접 요일을 **하루 전/하루 뒤** 라벨로(±1 달력일, Mon/Sun 경계는 주를 넘김; 과거나 다다다음 주처럼 표현 불가하면 생략 — 요일어는 미래만 가리킴) ② **나머지 두 주**의 같은 요일을 추천(현재 주 중복 안 함). 예) `금요일`→하루 전·하루 뒤 + 다음 주·다다음 주 / `다음 주 금요일`→하루 전·하루 뒤(다음 주 목/토) + 이번 주·다다음 주. `이번 주 X요일`은 파서가 "이번 주"를 소비 못 해 **bare {요일}로 치환**.
    - **파서 확장:** `다다음 주 {요일}` 미지원이던 걸 추가(`Korean.cs` `nnwwd`/`weekafternext` 그룹 + `ParseContext.WeekdayInWeeksAhead(target, weeksAhead)`; `NextWeekWeekday`는 이를 호출하도록 일반화). +2 ISO주로 클린 소비. `PARSING.md` 코퍼스에 행 추가.
  - **시각:** **해석값 기준 ±1시간**과 **오전/오후 토글**만(고정 프리셋 제거). 해석된 24h(`QuickAddPreview.Hour/Minute`)에서 계산, 명시 `오전/오후 H시[ M분]`로 치환(라운드트립 모호성 차단). 해석 시각이 없으면 헤더+되돌리기만.
  - **반복:** 매일·평일 + (요일 있을 때) 매주/격주 {요일}(요일 유지 빈도 전환). **언젠가:** 되돌리기만.
  - 현재 토큰과 동일한 프리셋은 자동 숨김(no-op 방지).
  - **치환→재파싱 모델 안전장치:** 생성되는 모든 문구(프리셋·인접 요일 7종·다음 주 {요일} 클린 소비·명시 시각 자정/정오 포함)가 실제 재인식되는지 `QuickAddPresetTests`(28건)로 고정. 헛도는 문구 0.
- **여전한 갭:** 토큰 단색(밑줄/굵기 등 색 외 신호 미적용 — 목표 2). 팝오버 헤더는 disabled MenuFlyoutItem(네이티브 muted 헤더) — 추후 커스텀 스타일 여지.

### 단계 5 — 남은 수동 검증(GUI/IME 필요)
- [ ] 토큰 탭 시 팝오버가 토큰 근처에 뜨고 caret이 토큰 안으로 들어가는지(Tapped 수신 확인).
- [ ] 되돌리기 → 액센트 즉시 사라지고, 이어 타이핑/커밋해도 그 단어가 **다시 날짜로 안 빨려가고 제목에 남는지**.
- [ ] 대안 선택 시 텍스트 치환·재파싱·caret·재틴트가 일관된지.
- [ ] 한글 조합 중 탭/팝오버가 조합을 깨지 않는지.
- [ ] **알려진 갭:** 토큰은 아직 단색뿐(밑줄/굵기 등 색 외 신호 미적용, 계획 목표 2 — 후속).

---

## 진행 상태
- **완료(코드+CI):** 단계 0~4, 단계 5(5.1 plumbing/커밋, 5.2 팝오버 + 내용 재정리·값 기반 대안, 서식 UI 차단). 빌드 그린, 413 테스트 통과.
- **다음:** 단계 5 수동 GUI/IME 검증(위 체크리스트). 색 외 접근성 신호(밑줄/굵기)는 후속.
