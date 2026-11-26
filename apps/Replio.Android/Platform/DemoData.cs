#if REPLIO_DEMO
using ReplioDesktop.Models;
using ReplioDesktop.Services;

namespace Replio.Android.Platform;

/// <summary>
/// Only in the emulator build of CI (-p:ReplioDemo=true): a few groups and sessions on a fresh install, so the
/// screenshots of the screens show real-looking lists. The phone APK has none of this.
/// </summary>
public static class DemoData
{
    public static void Seed()
    {
        try
        {
            var sessions = new JsonSessionStore();
            if (sessions.LoadSessions().Count > 0)
            {
                return;
            }

            var sales = new SessionTopic { Name = "Продажи" };
            var project = new SessionTopic { Name = "Проект «Альфа» с подрядчиком" };
            new JsonTopicStore().Save([sales, project]);

            DateTime today = DateTime.Today;
            Add(sessions, "Звонок с клиентом о поставке оборудования", today.AddHours(10).AddMinutes(5), 24, sales.Id);
            Add(sessions, "Планёрка", today.AddHours(9).AddMinutes(30), 12, string.Empty);
            Add(sessions, "Обсуждение сроков и стоимости проекта на следующий квартал с подрядчиком и юристом", today.AddDays(-1).AddHours(16), 47, project.Id);
            Add(sessions, "Собеседование", today.AddDays(-1).AddHours(11), 35, string.Empty);
            Add(sessions, "Встреча с поставщиком", today.AddDays(-3).AddHours(15), 18, sales.Id);
            PhonePlatform.Log("[demo] Demo groups and sessions added.");
        }
        catch (Exception ex)
        {
            PhonePlatform.Log("[demo] Demo data failed: " + ex);
        }
    }

    private static void Add(JsonSessionStore store, string title, DateTime started, double minutes, string topicId)
    {
        var session = new SessionSummary
        {
            Title = title,
            TopicId = topicId,
            StartedAt = started,
            EndedAt = started.AddMinutes(minutes),
            Status = SessionStatus.Completed,
            SummaryText = "## Итоги\n\n- Договорились о поставке до 15 декабря.\n- Стоимость: 1,2 млн ₽, оплата двумя частями.\n- Следующий шаг: прислать договор до пятницы.",
            Transcript =
            [
                Line(started, "Добрый день! Давайте обсудим сроки поставки оборудования."),
                Line(started.AddMinutes(1), "Мы готовы отгрузить до 15 декабря, если подпишем договор на этой неделе."),
                Line(started.AddMinutes(2), "А какая итоговая стоимость с доставкой и монтажом?"),
                Line(started.AddMinutes(3), "Один миллион двести тысяч, оплата двумя частями.")
            ],
            Usage = new SessionUsage { SessionDurationMinutes = minutes }
        };
        store.WriteSession(session.Id, store.Serialize(session));
    }

    private static SessionTranscriptItem Line(DateTime at, string text) => new()
    {
        Timestamp = at,
        Source = SessionSource.Mic,
        RawText = text,
        Text = text,
        FormattedText = text
    };
}
#endif
