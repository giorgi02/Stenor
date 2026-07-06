namespace Stenor.Interfaces;

/// <summary>Tray-balloon error reporting, implemented by the tray icon in Stenor.App.</summary>
public interface ITrayNotifier
{
    void ShowError(string title, string message);
}
