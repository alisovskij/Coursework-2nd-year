using System;
using System.Windows.Forms;

namespace WebCrawler
{
    /// <summary>
    /// Точка входа приложения.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }
}
