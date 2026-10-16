using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Sdk
{
    /// <summary>
    /// Universe Design Tool (designer.exe) oturumu.
    ///
    /// - Designer 64-bit ayrı süreçte çalışır; x86 uygulamamızdan geç bağlama (dynamic / IDispatch)
    ///   ile erişilir (tipli Interop E_NOINTERFACE verir).
    /// - Her universe okunduktan sonra Close() edilir ve COM referansları bırakılır.
    /// - Dispose: Quit + COM bırakma; süreç 10 sn içinde kapanmazsa SADECE bizim başlattığımız
    ///   designer.exe sonlandırılır. Böylece arka planda designer.exe birikmez.
    /// </summary>
    public sealed class DesignerHost : IDisposable
    {
        private dynamic app;
        private readonly int? pid;

        private DesignerHost(dynamic app, int? pid)
        {
            this.app = app;
            this.pid = pid;
        }

        public int? ProcessId => pid;

        public static DesignerHost Start()
        {
            var before = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName("designer")) before.Add(p.Id);

            Type t = Type.GetTypeFromProgID("Designer.Application", true);
            dynamic app = Activator.CreateInstance(t);

            int? pid = null;
            foreach (var p in Process.GetProcessesByName("designer"))
                if (!before.Contains(p.Id)) { pid = p.Id; break; }

            var host = new DesignerHost(app, pid);
            try
            {
                app.Visible = false;
                app.Interactive = false;   // diyalog açıp beklemesin
            }
            catch
            {
                host.Dispose();
                throw;
            }
            return host;
        }

        public void Logon(Credential credential, string cms, string authType)
        {
            app.Logon(credential.Username, credential.Password, cms, authType);
        }

        /// <summary>Universe'ü CMS'ten açar, tüm class/object'leri okur ve kapatır.</summary>
        public List<UniverseObject> Read(UniverseInfo universe)
        {
            dynamic universes = null;
            dynamic unv = null;
            try
            {
                universes = app.Universes;
                unv = universes.OpenFromEnterprise(universe.FolderPath, universe.Name);

                var result = new List<UniverseObject>();
                dynamic classes = unv.Classes;
                if (classes != null)
                {
                    foreach (dynamic cls in classes)
                    {
                        Collect(cls, null, result);
                        Release(cls);
                    }
                    Release(classes);
                }
                return result;
            }
            finally
            {
                if (unv != null)
                {
                    try { unv.Close(); } catch (Exception ex) { Console.WriteLine("[UYARI] Universe kapatılamadı: " + ex.Message); }
                    Release(unv);
                }
                Release(universes);
            }
        }

        private static void Collect(dynamic cls, string parentPath, List<UniverseObject> result)
        {
            string name = Convert.ToString(cls.Name);
            string path = parentPath == null ? name : parentPath + "\\" + name;

            dynamic objects = cls.Objects;
            if (objects != null)
            {
                foreach (dynamic o in objects)
                {
                    result.Add(new UniverseObject
                    {
                        Id = Convert.ToString(o.Id),
                        Name = Convert.ToString(o.Name),
                        Path = path,
                        Select = Clean(Convert.ToString(o.Select)),
                        Where = Clean(Convert.ToString(o.Where)),
                        State = Convert.ToBoolean(o.Show) ? "ACTIVE" : "HIDDEN",
                        Qualification = TryInt(() => o.Qualification)
                    });
                    Release(o);
                }
                Release(objects);
            }

            dynamic subClasses = cls.Classes;
            if (subClasses != null)
            {
                foreach (dynamic sub in subClasses)
                {
                    Collect(sub, path, result);
                    Release(sub);
                }
                Release(subClasses);
            }
        }

        private static int? TryInt(Func<object> get)
        {
            try { return Convert.ToInt32(get()); } catch { return null; }
        }

        private static string Clean(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return null;
            return sql.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        private static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o))
            {
                try { Marshal.FinalReleaseComObject(o); } catch { /* yoksay */ }
            }
        }

        public void Dispose()
        {
            if (app != null)
            {
                try { app.Quit(); } catch (Exception ex) { Console.WriteLine("[UYARI] Designer Quit: " + ex.Message); }
                Release((object)app);
                app = null;
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (pid.HasValue)
            {
                try
                {
                    var proc = Process.GetProcessById(pid.Value);
                    if (!proc.WaitForExit(10000))
                    {
                        proc.Kill();
                        Console.WriteLine("[UYARI] designer.exe (PID " + pid + ") kapanmadı, sonlandırıldı.");
                    }
                }
                catch (ArgumentException) { /* zaten kapanmış */ }
                catch (InvalidOperationException) { /* zaten kapanmış */ }
            }
        }
    }
}
