using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace jellyfin_anidoki;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Notifications.NotificationEventStore>();
        serviceCollection.AddHostedService<SessionServerEntry>();
        serviceCollection.AddHostedService<UserDataServerEntry>();
    }
}
