#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace jellyfin_anidoki.Configuration;

internal static class PluginPagesRuntime
{
    internal static void RemovePage(string id)
    {
        var interfaceType = AssemblyLoadContext.All.SelectMany(context => context.Assemblies)
            .Distinct()
            .Select(assembly => assembly.GetType("Jellyfin.Plugin.PluginPages.PluginInterface", false))
            .FirstOrDefault(type => type != null);
        var remove = interfaceType?.GetMethod("RemovePage", BindingFlags.Public | BindingFlags.Static,
            binder: null, types: new[] { typeof(string) }, modifiers: null);
        remove?.Invoke(null, new object[] { id });
    }
}
