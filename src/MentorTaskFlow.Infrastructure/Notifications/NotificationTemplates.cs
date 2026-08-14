using System.Net;
using System.Text.Json;
using MentorTaskFlow.Domain.Notifications;

namespace MentorTaskFlow.Infrastructure.Notifications;

/// <summary>A rendered message.</summary>
public sealed record RenderedMessage(string Subject, string PlainText, string Html);

/// <summary>
/// Russian templates for the event catalog (<c>NTF-019</c>).
/// </summary>
/// <remarks>
/// <para>
/// Versioned with the code rather than stored in the database: a template referring to a payload field
/// that no longer exists must fail review, not production.
/// </para>
/// <para>
/// <c>NTF-018</c> fixes the minimum content — what happened, to which task, by when — and
/// <c>NTF-017</c> the maximum: no tokens, no presigned URLs, no third-party personal data. The link
/// goes to the application, where access is checked again, and never to storage.
/// </para>
/// </remarks>
public static class NotificationTemplates
{
    public static RenderedMessage Render(string eventType, JsonDocument payload, string appBaseUrl)
    {
        var body = payload.RootElement;

        var (subject, lead) = eventType switch
        {
            NotificationEventTypes.AssignmentAssigned =>
                ("Вам назначена новая задача", "Вам назначена задача."),

            NotificationEventTypes.AssignmentSuggested =>
                ("Планировщик предложил задачи", "Планировщик подготовил предложение по задаче."),

            NotificationEventTypes.AssignmentReassigned =>
                ("Исполнитель задачи изменён", "Исполнитель задачи изменён."),

            NotificationEventTypes.SubmissionUploaded =>
                ("Загружена работа на проверку", "Ментор загрузил работу на проверку."),

            NotificationEventTypes.LateSubmissionUploaded =>
                ("Загружена работа с опозданием", "Ментор загрузил работу после дедлайна."),

            NotificationEventTypes.ReviewApproved =>
                ("Работа принята", "Ваша работа принята."),

            NotificationEventTypes.ReviewNeedsRework =>
                ("Работа возвращена на доработку", "Работа возвращена на доработку, назначен новый срок."),

            NotificationEventTypes.DeadlineReminder =>
                ("Приближается дедлайн", "Срок сдачи задачи приближается."),

            NotificationEventTypes.AssignmentOverdue =>
                ("Задача просрочена", "Срок сдачи задачи истёк."),

            NotificationEventTypes.AssignmentCancelled =>
                ("Задача отменена", "Задача отменена."),

            NotificationEventTypes.SchedulerNoActiveMentor =>
                ("В категории нет активных менторов", "Планировщик не нашёл активных менторов в категории."),

            NotificationEventTypes.CategoryWithoutLead =>
                ("В категории нет активного тимлида", "Категория осталась без активного тимлида."),

            NotificationEventTypes.BranchDeactivated =>
                ("Филиал деактивирован", "Филиал деактивирован: операции записи в его контуре недоступны."),

            NotificationEventTypes.BranchActivated =>
                ("Филиал снова активен", "Филиал активирован."),

            NotificationEventTypes.UserBranchChanged =>
                ("Вы переведены в другой филиал", "Вы переведены в другой филиал. Войдите в систему заново."),

            NotificationEventTypes.BranchWithoutAdmin =>
                ("В филиале нет администратора", "Филиал остался без активного администратора."),

            NotificationEventTypes.OrganizationSystemAlert =>
                ("Системное оповещение", "Зафиксировано событие, требующее внимания администратора."),

            NotificationEventTypes.NotificationDeadLetter =>
                ("Уведомления не доставляются", "Часть уведомлений не удалось доставить."),

            NotificationEventTypes.UserInvitation =>
                ("Приглашение в MentorTaskFlow", "Для вас создана учётная запись."),

            _ => ("Уведомление MentorTaskFlow", "Произошло событие в системе."),
        };

        var lines = new List<string> { lead };

        if (TryGetString(body, "assignmentTitle") is { } title)
        {
            lines.Add($"Задача: {title}");
        }

        if (TryGetString(body, "branchName") is { } branchName)
        {
            lines.Add($"Филиал: {branchName}");
        }

        if (TryGetString(body, "categoryName") is { } categoryName)
        {
            lines.Add($"Категория: {categoryName}");
        }

        // UX-001: the moment is formatted in the category's zone with the zone named, because a
        // deadline shown without one is read differently by a mentor travelling and their Lead.
        if (TryGetString(body, "dueAtLocal") is { } dueAtLocal)
        {
            lines.Add($"Срок: {dueAtLocal}");
        }

        if (TryGetString(body, "subjectFullName") is { } subjectName)
        {
            lines.Add($"Сотрудник: {subjectName}");
        }

        if (body.TryGetProperty("isLate", out var isLate) && isLate.ValueKind is JsonValueKind.True)
        {
            lines.Add("Работа сдана с опозданием.");
        }

        if (body.TryGetProperty("failedCount", out var failed) && failed.TryGetInt32(out var count))
        {
            lines.Add($"Не доставлено уведомлений за период: {count}.");
        }

        lines.Add($"Открыть в приложении: {appBaseUrl}");

        var plain = string.Join("\n", lines);
        var html = string.Join("<br/>", lines.Select(WebUtility.HtmlEncode));

        return new RenderedMessage(subject, plain, $"<p>{html}</p>");
    }

    private static string? TryGetString(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;
}
