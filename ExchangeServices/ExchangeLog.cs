using System;

namespace ExchangeServices
{
    /// <summary>
    /// Opt-in diagnostic logging for exchange HTTP calls.
    ///
    /// OFF by default so it never floods a server's stdout/journald (which was
    /// filling /var/log with millions of lines a day). A developer turns it on
    /// LOCALLY by setting the environment variable <c>EXCHANGE_VERBOSE=1</c>
    /// (or true/yes). This replaces the raw <c>Console.WriteLine</c> calls the
    /// exchange clients used for debugging.
    /// </summary>
    public static class ExchangeLog
    {
        private static readonly bool Verbose = IsEnabled();

        private static bool IsEnabled()
        {
            var v = (Environment.GetEnvironmentVariable("EXCHANGE_VERBOSE") ?? string.Empty).Trim();
            return v == "1" ||
                   v.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   v.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Writes a diagnostic line only when EXCHANGE_VERBOSE is enabled.</summary>
        public static void Debug(string message)
        {
            if (Verbose)
            {
                Console.WriteLine(message);
            }
        }
    }
}
