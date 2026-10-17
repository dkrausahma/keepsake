using System;
using System.Reflection;

namespace Keepsake.Core
{
    public static class ProgramInfo
    {
        public static string Version { get; } =
            typeof(ProgramInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? typeof(ProgramInfo).Assembly.GetName().Version.ToString();

        public static string Name => "Keepsake " + Version;

        public static string Host => Environment.MachineName;

        public static string User => Environment.UserDomainName + "\\" + Environment.UserName;
    }
}
