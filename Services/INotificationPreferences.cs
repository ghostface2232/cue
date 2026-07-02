namespace Cue.Services;

/// <summary>Minimal settings surface consumed by notification scheduling.</summary>
public interface INotificationPreferences
{
    bool NotificationsEnabled { get; }

    event EventHandler? NotificationsEnabledChanged;
}
