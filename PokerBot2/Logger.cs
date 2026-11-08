using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PokerBot2
{
    internal class Logger
    {
        [Conditional("DEBUG")]
        public static void DebugOnlyConsoleLog(string message)
        {
            Console.WriteLine(message);
        }
    }
}
