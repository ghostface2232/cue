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

---

## 진행 상태
- **완료:** 단계 0~4 (전부 테스트/빌드 그린, 383 테스트 통과).
- **다음(단계 5):** 토큰 클릭 팝오버(대안/원문 되돌리기) + `suppressedSpans`를 라이브 재틴트·커밋에 연결 + 저장 직전 재파싱(라이브 파스는 표시용 캐시). 4.3의 생산자·소비자가 여기서 붙음.
