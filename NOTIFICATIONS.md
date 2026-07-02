# Cue 리마인더 및 알림 기능 구현 문서

## 전체 구조 요약

### 설계 결정

- 알림은 별도 개념이 아니라 timed When(시각이 있는 예정일)의 파생 속성입니다. 시각이 있는 작업은 기본적으로 알림이 울립니다.
- TaskItem에 단일 enum 필드 Reminder(타입 ReminderTiming)를 추가합니다. 값은 AtTime(기본, 정시), TenMinutesBefore, OneHourBefore, OneDayBefore, None(끔)의 다섯 가지입니다. enum 기본값 0을 AtTime으로 두어 기존 JSON 레코드가 마이그레이션 없이 정시 알림으로 역직렬화되게 합니다.
- 종일(IsAllDay) 작업은 개별 알림이 없고, 옵트인 데일리 브리핑이 커버합니다.
- 작업당 알림은 하나(단일 선택)입니다. 다중 알림은 만들지 않습니다.
- overdue 재알림, 미완료 조르기(nag)는 만들지 않습니다. 지나간 작업의 가시성은 앱 안의 오늘 뷰가 담당합니다.
- 스누즈는 직접 구현하지 않고 Windows 토스트의 시스템 스누즈(selection input + activationType이 system인 snooze 액션)에 위임합니다. OS가 재예약을 수행하므로 앱이 꺼져 있어도 동작합니다.

### 기술 스택

- 알림 API: Microsoft.Toolkit.Uwp.Notifications 7.1.3 (NuGet 최신 안정 버전, net5.0-windows10.0.17763.0 타깃 포함으로 .NET 10 windows TFM 호환). 이 타깃의 레거시 System.Drawing.Common 4.7.0 전이 의존성은 보안 수정 버전 4.7.2로 앱 프로젝트에서 재고정합니다.
- 이 패키지를 선택한 근거: Windows App SDK의 AppNotifications에는 예약(Schedule) API가 없고, AppNotificationManager는 Singleton 패키지에 의존하여 self-contained 배포와 충돌합니다. 반면 이 패키지의 ScheduledToastNotification 경로는 package identity, Singleton, WinAppSDK 컴포넌트 추가가 전부 불필요하며 unpackaged 앱에서 앱이 종료된 상태에도 OS가 알림을 전달합니다.
- 주의: 이 패키지는 유지보수 모드(원 저장소 아카이브)입니다. 따라서 앱 코드가 패키지 API를 직접 부르지 않도록 어댑터 인터페이스 뒤에 격리하고, 장기적으로 자체 구현(AUMID + COM 등록 + Windows.UI.Notifications 직접 호출)으로 교체 가능하게 합니다.

### 아키텍처(레이어 배치와 데이터 흐름)

```
Domain (ReminderTiming enum, TaskItem.Reminder 필드)
  ↑
Storage (변경 없음 — Save 이벤트만 노출)
  ↑
ViewModels (상세 패널 드롭다운, 설정 토글 바인딩)
  ↑
App (NotificationScheduler 서비스, 토스트 어댑터, 활성화 라우팅)
```

데이터 흐름: ITaskStore.Save(단일 쓰기 통로)가 발생할 때마다, 그리고 앱 시작 시마다 NotificationScheduler가 조정(reconcile) 루프를 돕니다. 앞으로 14일 창 안에서 알림이 필요한 작업/occurrence를 열거해 기대 집합을 만들고, OS에 등록된 예약 토스트 집합(GetScheduledToastNotifications)과 diff하여 추가/제거합니다. 토스트의 Tag를 작업 id(반복이면 결정론적 occurrence id)에서 유도하여 diff를 멱등으로 만듭니다.

역방향 흐름: 토스트의 완료 버튼 클릭 → (앱이 꺼져 있으면 프로세스 기동) → 단일 인스턴스 리디렉션 → ITaskStore.Save 경유 완료 처리. 토스트 본문 클릭 → 앱 활성화 후 해당 작업으로 이동.

### 단계 개요

| 단계 | 내용 | 커밋 단위 |
|---|---|---|
| 0 | AGENTS.md 명세 정비(잘못된 전제 수정 + 알림 아키텍처 불변 추가) | docs |
| 1 | 도메인: ReminderTiming enum + TaskItem.Reminder | feat |
| 2 | 단일 인스턴스화(AppInstance 리디렉션) | feat |
| 3 | 토스트 인프라 어댑터 + 등록 + 활성화 라우팅 | feat |
| 4 | NotificationScheduler 조정 루프 | feat |
| 5 | 토스트 콘텐츠와 액션(완료, 시스템 스누즈) | feat |
| 6 | 반복 작업 occurrence 예약 | feat |
| 7 | UI: 상세 패널 드롭다운, 설정 알림 섹션, 퀵애드 팝오버 | feat |
| 8 | 데일리 브리핑(옵트인) | feat |
| 9 | Inno Setup 언인스톨 정리 + 클린 VM 검증 | chore |
