using System.Reflection;

namespace Kakikomi.Updates;

public static class AppVersionReader
{
    public static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? informational[..plus] : informational;
        }

        var file = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.IsNullOrWhiteSpace(file))
        {
            var parts = file.Split('.');
            if (parts.Length >= 3)
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            return file;
        }

        return "0.0.0";
    }
}
