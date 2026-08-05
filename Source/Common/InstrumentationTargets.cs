using System.Reflection;

namespace Multiplayer.Common
{
    /// <summary>
    /// Decides which methods the "Log all patch" debug action may attach its call logger to.
    ///
    /// Kept here rather than beside the debug action so it can be tested. The test project cannot
    /// reference the client, which needs the game's own assemblies to load.
    /// </summary>
    public static class InstrumentationTargets
    {
        /// <summary>The logger's own prefix, which must never instrument itself.</summary>
        public const string LoggerMethodName = "MultiplayerMethodCallLogger";

        /// <summary>
        /// Whether a call logger can be attached to <paramref name="method"/>.
        ///
        /// Contract: exclude anything Harmony cannot build a wrapper around. Harmony patches by emitting
        /// a replacement that wraps the original's body, so a method with no body to wrap produces
        /// malformed IL and the runtime rejects it.
        ///
        /// NOT YET COMPLETE. Extern methods are not excluded, which is what
        /// InstrumentationTargetsTest demonstrates.
        /// </summary>
        public static bool ShouldInstrument(MethodBase method)
        {
            if (method == null)
                return false;

            if (method.Name == LoggerMethodName)
                return false;

            // Property getters are noise: they run constantly and say nothing about control flow.
            if (method.Name.StartsWith("get_"))
                return false;

            if (method.IsAbstract)
                return false;

            if (method.IsGenericMethod)
                return false;

            var declaring = method.DeclaringType;
            if (declaring == null || declaring.IsGenericType)
                return false;

            if (declaring.BaseType == typeof(System.MulticastDelegate))
                return false;

            return true;
        }
    }
}
