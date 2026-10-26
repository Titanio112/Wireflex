using Wireflex.Core.Connection;
using Wireflex.Core.Decisions;
using Wireflex.Core.Monitoring;

namespace Wireflex.App;

/// <summary>Qualquer mudança no coordenador atualiza o tooltip da bandeja.</summary>
internal sealed class TrayObserver(Func<TrayIcon?> tray) : IConnectionObserver
{
    public void OnDeviceEvent(DeviceEvent deviceEvent) => tray()?.RequestRefresh();

    public void OnAction(EngineAction action) => tray()?.RequestRefresh();

    public void OnNote(string message)
    {
    }
}
