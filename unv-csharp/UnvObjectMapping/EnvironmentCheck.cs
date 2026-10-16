using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnvObjectMapping
{
    /// <summary>
    /// Ortam kontrolü: süreç bitness'i, BO .NET SDK DLL'lerinin nereden yüklendiği,
    /// Designer'ın geç bağlama ile başlatılıp garanti kapatılması.
    /// </summary>
    internal static class EnvironmentCheck
    {
        public static void Run()
        {
            Console.WriteLine("CLR sürümü      : " + Environment.Version);
            Console.WriteLine("64-bit süreç mi : " + Environment.Is64BitProcess + "  (False olmalı)");
            Console.WriteLine();

            Check("Enterprise.Framework", () => typeof(CrystalDecisions.Enterprise.SessionMgr).Assembly.Location);
            Check("Enterprise.InfoStore", () => typeof(CrystalDecisions.Enterprise.InfoStore).Assembly.Location);
            Console.WriteLine();

            CheckDesignerStartStop();
        }

        private static void Check(string label, Func<string> location)
        {
            try
            {
                Console.WriteLine("[OK]   " + label + "\n       <- " + location());
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HATA] " + label + " : " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        /// <summary>Designer'ı geç bağlama ile başlatır, sürümünü okur, kapatır ve sürecin kapandığını doğrular.</summary>
        private static void CheckDesignerStartStop()
        {
            var before = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("designer")) before.Add(p.Id);

            dynamic app = null;
            int? ourPid = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Designer.Application", true);
                app = Activator.CreateInstance(t);

                // Bizim başlattığımız designer.exe'yi bul (gerekirse sonradan sonlandırmak için)
                foreach (var p in Process.GetProcessesByName("designer"))
                    if (!before.Contains(p.Id)) { ourPid = p.Id; break; }

                app.Visible = false;
                app.Interactive = false;
                Console.WriteLine("[OK]   Designer başlatıldı. Sürüm: " + app.Version + "  PID: " + ourPid);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HATA] Designer: " + ex.GetType().Name + " - " + ex.Message);
            }
            finally
            {
                if (app != null)
                {
                    try { app.Quit(); } catch (Exception ex) { Console.WriteLine("[UYARI] Quit: " + ex.Message); }
                    Marshal.FinalReleaseComObject((object)app);
                    app = null;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // Quit sonrası 10 sn bekle, hâlâ açıksa sadece BİZİM sürecimizi sonlandır
                if (ourPid.HasValue)
                {
                    try
                    {
                        var proc = Process.GetProcessById(ourPid.Value);
                        if (!proc.WaitForExit(10000))
                        {
                            proc.Kill();
                            Console.WriteLine("[UYARI] designer.exe kendiliğinden kapanmadı, sonlandırıldı.");
                        }
                    }
                    catch (ArgumentException) { /* süreç zaten kapanmış */ }
                }
            }

            int after = Process.GetProcessesByName("designer").Length;
            Console.WriteLine((after <= before.Count ? "[OK]   " : "[UYARI] ")
                + "Çalışan designer.exe sayısı: önce " + before.Count + ", sonra " + after);
        }
    }
}
