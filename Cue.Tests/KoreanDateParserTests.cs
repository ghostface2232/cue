using Cue.Domain;
using Cue.Parsing;

namespace Cue.Tests;

/// <summary>
/// Representative coverage of the Korean quick-add parser across the PARSING.md categories:
/// relative/weekday/absolute scheduled dates, times and the evening flag, deadlines, recurrence,
/// someday, unscheduled, and the misrecognition guards. The reference instant is fixed so the
/// relative expressions resolve deterministically; the zone is UTC so local == the wall clock used.
/// </summary>
public sealed class KoreanDateParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);
    private const string Tz = "UTC";
    private static readonly DateOnly Today = new(2026, 6, 23);

    private readonly IDateParser _parser = new KoreanDateParser();

    private ParsedQuickAdd Parse(string input) => _parser.Parse(input, Now, Tz);

    private static DateOnly WhenDate(ScheduledWhen w) => DateOnly.FromDateTime(w.Date!.Value.ToLocal().DateTime);
    private static int WhenHour(ScheduledWhen w) => w.Date!.Value.ToLocal().Hour;
    private static int WhenMinute(ScheduledWhen w) => w.Date!.Value.ToLocal().Minute;
    private static DateOnly ZonedDate(ZonedDateTime z) => DateOnly.FromDateTime(z.ToLocal().DateTime);

    // ---- 1. Relative scheduled dates ----------------------------------------

    [Theory]
    [InlineData("내일 장보기", "장보기", 1)]
    [InlineData("모레 치과 예약 확인하기", "치과 예약 확인하기", 2)]
    [InlineData("오늘 빨래 돌리기", "빨래 돌리기", 0)]
    [InlineData("3일 후 프로젝트 중간 점검", "프로젝트 중간 점검", 3)]
    [InlineData("2주 후 건강검진 받기", "건강검진 받기", 14)]
    [InlineData("내일모레 친구 선물 사기", "친구 선물 사기", 2)]
    [InlineData("일주일 뒤에 전세 계약 연장 문의", "전세 계약 연장 문의", 7)]
    public void RelativeDate_BecomesOnDate_WithCleanTitle(string input, string title, int offsetDays)
    {
        var r = Parse(input);
        Assert.Equal(title, r.Title);
        Assert.Equal(WhenKind.OnDate, r.When.Kind);
        Assert.Equal(Today.AddDays(offsetDays), WhenDate(r.When));
        Assert.Null(r.Deadline);
        Assert.Null(r.Recurrence);
    }

    // ---- 2. Weekday / absolute dates ----------------------------------------

    [Fact]
    public void Weekday_ResolvesToUpcomingThatWeekday()
    {
        var r = Parse("금요일에 미용실 가기");
        Assert.Equal("미용실 가기", r.Title);
        Assert.Equal(WhenKind.OnDate, r.When.Kind);
        Assert.Equal(DayOfWeek.Friday, WhenDate(r.When).DayOfWeek);
        Assert.True(WhenDate(r.When) >= Today);
    }

    [Fact]
    public void NextWeekWeekday_LandsInTheFollowingWeek()
    {
        var r = Parse("다음 주 월요일 출근길에 우산 챙기기");
        Assert.Equal("출근길에 우산 챙기기", r.Title);
        Assert.Equal(DayOfWeek.Monday, WhenDate(r.When).DayOfWeek);
        Assert.True(WhenDate(r.When) > Today);
    }

    [Fact]
    public void AbsoluteMonthDay_ResolvesToThatDate()
    {
        var r = Parse("3월 15일 동창 모임 참석");
        Assert.Equal("동창 모임 참석", r.Title);
        Assert.Equal(3, WhenDate(r.When).Month);
        Assert.Equal(15, WhenDate(r.When).Day);
    }

    // ---- 3. Times + 4. evening flag -----------------------------------------

    [Fact]
    public void Time_IsCapturedOnTheScheduledDate()
    {
        var r = Parse("내일 오후 3시 회의 준비");
        Assert.Equal("회의 준비", r.Title);
        Assert.Equal(Today.AddDays(1), WhenDate(r.When));
        Assert.Equal(15, WhenHour(r.When));
    }

    [Fact]
    public void HalfHour_ResolvesToThirtyMinutes()
    {
        var r = Parse("오후 4시 반 아이 하원 픽업");
        Assert.Equal("아이 하원 픽업", r.Title);
        Assert.Equal(Today, WhenDate(r.When));
        Assert.Equal(16, WhenHour(r.When));
        Assert.Equal(30, WhenMinute(r.When));
    }

    [Fact]
    public void EveningTime_SetsTimeAndEveningFlag_WithoutDoubleCounting()
    {
        // The tricky case: "오늘 저녁 7시" is the time; the "저녁" inside "저녁 약속" must stay in the title.
        var r = Parse("오늘 저녁 7시 저녁 약속");
        Assert.Equal("저녁 약속", r.Title);
        Assert.Equal(Today, WhenDate(r.When));
        Assert.Equal(19, WhenHour(r.When));
        Assert.True(r.When.IsEvening);
    }

    [Theory]
    [InlineData("오늘 저녁 운동", "운동", 0)]
    [InlineData("저녁에 강아지 산책시키기", "강아지 산책시키기", 0)]
    public void EveningWord_RaisesEveningFlag(string input, string title, int offsetDays)
    {
        var r = Parse(input);
        Assert.Equal(title, r.Title);
        Assert.Equal(WhenKind.OnDate, r.When.Kind);
        Assert.Equal(Today.AddDays(offsetDays), WhenDate(r.When));
        Assert.True(r.When.IsEvening);
    }

    [Fact]
    public void BareHour_WhenMorningReadingHasPassed_IsTakenAsAfternoon()
    {
        // It's 10:00; a bare "3시" today is ambiguous, but 3am is already gone, so it means 3pm.
        var at10 = new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero);
        var r = _parser.Parse("오늘 3시 미팅", at10, Tz);
        Assert.Equal("미팅", r.Title);
        Assert.Equal(Today, WhenDate(r.When));
        Assert.Equal(15, WhenHour(r.When));
    }

    [Fact]
    public void BareHour_WhenMorningReadingStillAhead_StaysMorning()
    {
        // It's 01:00; 3am is still ahead today, so a bare "3시" keeps its morning reading.
        var at1 = new DateTimeOffset(2026, 6, 23, 1, 0, 0, TimeSpan.Zero);
        var r = _parser.Parse("오늘 3시 미팅", at1, Tz);
        Assert.Equal(3, WhenHour(r.When));
    }

    [Fact]
    public void BareHour_OnAFutureDate_StaysMorning()
    {
        // Tomorrow's 3am is never "past now", so a bare hour on a future date is left as morning.
        var at10 = new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero);
        var r = _parser.Parse("내일 3시 미팅", at10, Tz);
        Assert.Equal(Today.AddDays(1), WhenDate(r.When));
        Assert.Equal(3, WhenHour(r.When));
    }

    [Fact]
    public void ExplicitMeridiem_IsNeverBumped()
    {
        // "오전 3시" stays 3am even after 3am has passed — an explicit meridiem is authoritative.
        var at10 = new DateTimeOffset(2026, 6, 23, 10, 0, 0, TimeSpan.Zero);
        var r = _parser.Parse("오늘 오전 3시 미팅", at10, Tz);
        Assert.Equal(3, WhenHour(r.When));
    }

    [Fact]
    public void BareTimeWithoutDate_AlsoDisambiguatesAgainstNow()
    {
        // The date-less rule resolves to today; the same morning-passed bump applies.
        var at14 = new DateTimeOffset(2026, 6, 23, 14, 0, 0, TimeSpan.Zero);
        var r = _parser.Parse("3시 미팅", at14, Tz);
        Assert.Equal(Today, WhenDate(r.When));
        Assert.Equal(15, WhenHour(r.When));
    }

    // ---- 5. Deadlines -------------------------------------------------------

    [Fact]
    public void DeadlineParticle_RoutesToDeadline_NotWhen()
    {
        var r = Parse("금요일까지 보고서 제출");
        Assert.Equal("보고서 제출", r.Title);
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.NotNull(r.Deadline);
        Assert.Equal(DayOfWeek.Friday, ZonedDate(r.Deadline!.Value).DayOfWeek);
    }

    [Fact]
    public void DeadlineRelative_AndWithinDays()
    {
        var tomorrow = Parse("내일까지 과제 마무리");
        Assert.Equal("과제 마무리", tomorrow.Title);
        Assert.Equal(Today.AddDays(1), ZonedDate(tomorrow.Deadline!.Value));

        var within = Parse("3일 안에 교환 신청 넣기");
        Assert.Equal("교환 신청 넣기", within.Title);
        Assert.Equal(Today.AddDays(3), ZonedDate(within.Deadline!.Value));
        Assert.Equal(WhenKind.Unscheduled, within.When.Kind);
    }

    [Fact]
    public void DeadlineAbsoluteMonthDay()
    {
        var r = Parse("5월 31일까지 종합소득세 신고");
        Assert.Equal("종합소득세 신고", r.Title);
        Assert.Equal(5, ZonedDate(r.Deadline!.Value).Month);
        Assert.Equal(31, ZonedDate(r.Deadline!.Value).Day);
    }

    // ---- 6. Recurrence ------------------------------------------------------

    [Theory]
    [InlineData("매주 월요일 운동", "운동", "FREQ=WEEKLY;BYDAY=MO")]
    [InlineData("매월 1일 가계부 정산", "가계부 정산", "FREQ=MONTHLY;BYMONTHDAY=1")]
    [InlineData("매주 금요일 주간 회고 작성", "주간 회고 작성", "FREQ=WEEKLY;BYDAY=FR")]
    [InlineData("격주 수요일 스터디 모임", "스터디 모임", "FREQ=WEEKLY;INTERVAL=2;BYDAY=WE")]
    [InlineData("평일 아침 7시 기상", "기상", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData("매년 어머니 생신 챙기기", "어머니 생신 챙기기", "FREQ=YEARLY")]
    [InlineData("30분마다 일어나서 스트레칭", "일어나서 스트레칭", "FREQ=MINUTELY;INTERVAL=30")]
    public void Recurrence_BecomesRRule_WithCleanTitle(string input, string title, string rrule)
    {
        var r = Parse(input);
        Assert.Equal(title, r.Title);
        Assert.NotNull(r.Recurrence);
        Assert.Equal(rrule, r.Recurrence!.Rule);
        Assert.False(string.IsNullOrEmpty(r.Recurrence.Anchor.TimeZoneId));
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
    }

    // ---- 7/8. Unscheduled & Someday -----------------------------------------

    [Theory]
    [InlineData("새 노트북 알아보기")]
    [InlineData("거실 전구 갈기")]
    [InlineData("냉장고 유통기한 점검")]
    public void NoDateExpression_LeavesTitleWholeAndUnscheduled(string input)
    {
        var r = Parse(input);
        Assert.Equal(input, r.Title);
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.Null(r.Deadline);
        Assert.Null(r.Recurrence);
    }

    [Theory]
    [InlineData("언젠가 제주도 한 달 살기", "제주도 한 달 살기")]
    [InlineData("나중에 기타 배우기", "기타 배우기")]
    [InlineData("시간 날 때 옛날 사진 백업하기", "옛날 사진 백업하기")]
    [InlineData("여유 생기면 베란다 텃밭 가꾸기", "베란다 텃밭 가꾸기")]
    public void SomedayMarkers_BecomeSomeDay(string input, string title)
    {
        var r = Parse(input);
        Assert.Equal(title, r.Title);
        Assert.Equal(WhenKind.SomeDay, r.When.Kind);
        Assert.Null(r.Deadline);
    }

    // ---- 10. Misrecognition guards ------------------------------------------

    [Theory]
    [InlineData("3월의 라이온 만화책 사기")]
    [InlineData("내일로 기차표 예매하기")]
    [InlineData("오늘의집 앱에서 소파 주문")]
    [InlineData("24시 김밥집에 도시락 미리 주문")]
    [InlineData("일요일의 데이트 플레이리스트 만들기")]
    public void DateLikeWordsInsideTitle_AreNotMisread(string input)
    {
        var r = Parse(input);
        Assert.Equal(input, r.Title);
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.Null(r.Deadline);
        Assert.Null(r.Recurrence);
    }

    // ---- Out-of-range guards (RFC 5545 / calendar) --------------------------

    [Theory]
    [InlineData("매월 99일 가계부 정산")]   // BYMONTHDAY out of 1..31
    [InlineData("0분마다 스트레칭")]          // INTERVAL must be >= 1
    [InlineData("13월 40일 약속 잡기")]      // month/day out of range
    [InlineData("4월 31일 나들이")]           // day exceeds the month's length
    [InlineData("32일에 헬스장 등록")]        // day-of-month out of 1..31
    public void OutOfRangeDateOrRecurrence_StaysInTitle(string input)
    {
        var r = Parse(input);
        Assert.Equal(input, r.Title);
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.Null(r.Deadline);
        Assert.Null(r.Recurrence);
    }

    [Fact]
    public void BoundaryValid_MonthlyDay31_IsAccepted()
    {
        var r = Parse("매월 31일 정산");
        Assert.Equal("정산", r.Title);
        Assert.NotNull(r.Recurrence);
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=31", r.Recurrence!.Rule);
    }

    // ---- 9. Word order + 11. composite --------------------------------------

    [Fact]
    public void DateExpression_TrailingInTheSentence_StillRecognized()
    {
        var r = Parse("회의 준비 내일 오후 3시");
        Assert.Equal("회의 준비", r.Title);
        Assert.Equal(Today.AddDays(1), WhenDate(r.When));
        Assert.Equal(15, WhenHour(r.When));
    }

    [Fact]
    public void TrailingDeadline_IsRecognized()
    {
        var r = Parse("보고서 금요일까지 끝내기");
        Assert.Equal("보고서 끝내기", r.Title);
        Assert.Equal(DayOfWeek.Friday, ZonedDate(r.Deadline!.Value).DayOfWeek);
    }

    [Fact]
    public void Composite_SplitsScheduledDateAndDeadline()
    {
        var r = Parse("내일 오전에 자료 모아서 금요일까지 기획안 제출");
        Assert.Equal("자료 모아서 기획안 제출", r.Title);
        Assert.Equal(WhenKind.OnDate, r.When.Kind);
        Assert.Equal(Today.AddDays(1), WhenDate(r.When));
        Assert.NotNull(r.Deadline);
        Assert.Equal(DayOfWeek.Friday, ZonedDate(r.Deadline!.Value).DayOfWeek);
    }

    // ---- Robustness ---------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInput_IsTitleOnly(string input)
    {
        var r = Parse(input);
        Assert.Equal(WhenKind.Unscheduled, r.When.Kind);
        Assert.Null(r.Deadline);
        Assert.Null(r.Recurrence);
    }
}
