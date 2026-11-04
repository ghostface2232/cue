---
title: Cue 개인정보처리방침 / Privacy Policy
permalink: /privacy/
---

# Cue 개인정보처리방침

시행일: 2026년 11월 4일

Cue는 사용자의 할 일 데이터를 사용자의 PC에서 처리하는 로컬 우선(local-first) 애플리케이션입니다. Cue 개발자는 Cue를 통해 사용자의 개인정보나 할 일 데이터를 수집하지 않습니다.

## 1. 로컬에 저장되는 데이터

Cue에 입력한 다음 정보는 사용자의 PC에 로컬 파일로만 저장됩니다.

- 할 일 제목과 메모
- 날짜, 시간, 반복 일정 및 우선순위
- 그룹, 태그 및 체크리스트
- 완료 상태와 반복 일정의 완료 기록

할 일 데이터의 기본 저장 위치는 사용자의 `문서(Documents)\Cue` 폴더입니다. Cue는 이 데이터를 Cue 개발자 또는 Cue가 운영하는 서버로 전송하지 않습니다. Cue는 사용자의 데이터를 저장하거나 동기화하기 위한 서버를 운영하지 않습니다.

테마, 알림 여부 등의 앱 환경설정은 PC의 앱 전용 로컬 저장소에만 저장됩니다. Microsoft Store 버전은 Windows가 관리하는 앱별 로컬 설정에, GitHub에서 설치한 버전은 `%LOCALAPPDATA%\Cue\settings.json`에 저장합니다.

Cue는 `%LOCALAPPDATA%\Cue`에 다음 파일도 만듭니다. 이 파일들은 PC에만 있으며 외부로 전송되지 않습니다.

- 목록을 빠르게 표시하기 위한 인덱스(`index-*.db`). 할 일 제목과 체크리스트 등의 내용이 들어 있고, 언제든 할 일 데이터에서 다시 만들 수 있습니다.
- 문제 해결을 위한 진단 로그(`logs\cue.log`). 앱 버전, Windows 버전, 오류 내용, 파일 경로(Windows 사용자 이름이 포함될 수 있음)가 기록되며, 할 일 제목과 메모는 기록되지 않습니다. 설정의 정보 화면에서 폴더를 열 수 있습니다.

Microsoft Store 버전에서는 Windows가 이 위치를 앱별 저장소로 연결합니다.

사용자의 문서 폴더가 OneDrive 등 클라우드 저장소에 연결되거나 리디렉션되어 있다면, 해당 파일은 사용자가 설정한 Windows 또는 클라우드 서비스에 의해 동기화될 수 있습니다. 이는 Cue가 수행하는 전송이 아니며 해당 서비스의 설정과 개인정보처리방침이 적용됩니다.

## 2. 수집하지 않는 정보

Cue에는 다음 기능이 없으며 관련 정보를 수집하지 않습니다.

- 사용자 계정 또는 회원가입
- 로그인 또는 인증
- 사용 분석 및 텔레메트리
- 광고 또는 광고 식별자
- 위치 정보 수집
- 연락처, 사진, 카메라 또는 마이크 접근
- 개발자에게 전송되는 오류 및 충돌 보고

Cue 개발자는 Cue를 통해 사용자 데이터를 수집하지 않으므로 이를 판매, 대여하거나 제3자에게 제공하지 않습니다.

## 3. 알림

Cue의 할 일 알림은 Windows의 로컬 알림 기능을 사용하여 사용자의 PC에서 예약되고 표시됩니다. 알림을 위해 할 일 내용이 Cue 서버로 전송되지 않으며, Cue는 푸시 알림 서버를 운영하지 않습니다. 알림은 앱 설정에서 끌 수 있습니다.

## 4. 네트워크 사용

Cue는 할 일 데이터를 네트워크로 전송하지 않습니다.

GitHub에서 설치한 버전에서는 사용자가 설정 화면에서 업데이트 확인 또는 다운로드를 선택할 때 GitHub Releases에 연결할 수 있습니다. 이 과정에서 할 일 내용은 전송되지 않습니다. GitHub에는 일반적인 인터넷 연결 정보(예: IP 주소 및 요청 정보)가 전달될 수 있으며 [GitHub 개인정보처리방침](https://docs.github.com/ko/site-policy/privacy-policies/github-general-privacy-statement)이 적용됩니다.

Microsoft Store에서 설치한 버전의 배포와 업데이트는 Microsoft Store가 처리하며 [Microsoft 개인정보처리방침](https://privacy.microsoft.com/ko-kr/privacystatement)이 적용됩니다.

## 5. 데이터 보관 및 삭제

할 일 데이터는 사용자가 직접 관리하는 로컬 저장소에 보관됩니다. Cue에서 삭제한 항목은 데이터 일관성을 위해 로컬 파일에 삭제 표시가 남을 수 있지만 외부로 전송되지는 않습니다.

앱을 제거해도 `문서(Documents)\Cue` 폴더의 데이터는 자동으로 삭제되지 않을 수 있습니다. Cue의 할 일 데이터를 완전히 삭제하려면 Cue를 종료한 뒤 해당 폴더와 `%LOCALAPPDATA%\Cue` 폴더(인덱스, 진단 로그, GitHub 버전의 환경설정)를 직접 삭제해야 합니다.

## 6. 개인정보처리방침 변경

Cue의 데이터 처리 방식이 변경되면 이 문서를 업데이트하고 시행일을 변경합니다. 특히 향후 동기화 또는 온라인 서비스가 추가될 경우, 적용 전에 수집·전송되는 정보와 사용 목적을 이 문서에 명시합니다.

## 7. 문의

개인정보 관련 문의:

- 이메일: [baemingwan@gmail.com](mailto:baemingwan@gmail.com)
- GitHub 이슈: [github.com/ghostface2232/cue/issues](https://github.com/ghostface2232/cue/issues)

---

# Cue Privacy Policy (English)

Effective date: November 4, 2026

Cue is a local-first application that processes your task data on your PC. The developer of Cue does not collect personal information or task data through Cue.

## 1. Data stored locally

The following information entered in Cue is stored only in local files on your PC:

- Task titles and notes
- Dates, times, recurrence schedules, and priorities
- Groups, tags, and checklists
- Completion status and recurring-task history

By default, task data is stored in your `Documents\Cue` folder. Cue does not transmit this data to the developer or to any server operated by Cue. Cue does not operate a server for storing or synchronizing user data.

App preferences, such as theme and notification settings, are stored only in app-private local storage on your PC: the local settings Windows manages for the Microsoft Store version, and `%LOCALAPPDATA%\Cue\settings.json` for the version installed from GitHub.

Cue also creates the following files in `%LOCALAPPDATA%\Cue`. They stay on your PC and are not transmitted anywhere.

- An index (`index-*.db`) used to display lists quickly. It contains content such as task titles and checklists, and can always be rebuilt from your task data.
- A diagnostic log (`logs\cue.log`) for troubleshooting. It records the app and Windows versions, error details, and file paths (which may include your Windows user name). It does not record task titles or notes. You can open its folder from the About section in Settings.

For the Microsoft Store version, Windows maps this location to the app's private storage.

If your Documents folder is connected or redirected to a cloud storage service such as OneDrive, the files may be synchronized by Windows or by the cloud service you configured. This transfer is not performed by Cue and is subject to that service's settings and privacy policy.

## 2. Information Cue does not collect

Cue does not provide or collect information through any of the following:

- User accounts or registration
- Login or authentication
- Usage analytics or telemetry
- Advertising or advertising identifiers
- Location collection
- Access to contacts, photos, camera, or microphone
- Error or crash reports sent to the developer

Because the developer does not collect user data through Cue, the developer does not sell, rent, or disclose such data to third parties.

## 3. Notifications

Cue uses Windows local notification features to schedule and display task notifications on your PC. Task content is not sent to a Cue server for notifications, and Cue does not operate a push-notification server. Notifications can be disabled in the app settings.

## 4. Network use

Cue does not transmit task data over the network.

In the version installed from GitHub, Cue may connect to GitHub Releases when you choose to check for or download an update from Settings. Task content is not included in these requests. GitHub may receive ordinary connection information such as your IP address and request metadata, subject to the [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

Distribution and updates for the Microsoft Store version are handled by Microsoft Store and are subject to the [Microsoft Privacy Statement](https://privacy.microsoft.com/en-us/privacystatement).

## 5. Data retention and deletion

Task data remains in local storage controlled by you. Items deleted in Cue may remain marked as deleted in local files for data consistency, but they are not transmitted externally.

Uninstalling Cue may not automatically remove data in `Documents\Cue`. To delete all Cue task data, close Cue and manually delete that folder and the `%LOCALAPPDATA%\Cue` folder (the index, the diagnostic log, and the GitHub version's preferences).

## 6. Changes to this policy

If Cue's data practices change, this document and its effective date will be updated. In particular, if synchronization or other online services are introduced in the future, this policy will describe the information transmitted and its purpose before those features apply.

## 7. Contact

For privacy questions:

- Email: [baemingwan@gmail.com](mailto:baemingwan@gmail.com)
- GitHub Issues: [github.com/ghostface2232/cue/issues](https://github.com/ghostface2232/cue/issues)
