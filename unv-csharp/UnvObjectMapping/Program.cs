using System;
using System.Linq;
using UnvObjectMapping.App;
using UnvObjectMapping.Config;
using UnvObjectMapping.Credentials;
using UnvObjectMapping.Model;
using UnvObjectMapping.Output;
using UnvObjectMapping.Sdk;

namespace UnvObjectMapping
{
    internal static class Program
    {
        private const string Usage =
            "Kullanım:\n" +
            "  UnvObjectMapping envcheck\n" +
            "  UnvObjectMapping sbox <credentials.json>\n" +
            "  UnvObjectMapping logon <credentials.json>\n" +
            "  UnvObjectMapping read <credentials.json> [universe adında geçen metin]\n" +
            "  UnvObjectMapping dbtest <credentials.json> [universe adında geçen metin]\n" +
            "  UnvObjectMapping dbreport <credentials.json>\n" +
            "  UnvObjectMapping run <credentials.json>      <- ana çalıştırma (tüm ortamlar, tüm UNV'ler)";

        private const string TestSystem = "DBTEST_UNV";

        private const string AuthType = "secEnterprise";

        private static int Main(string[] args)
        {
            try
            {
                string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
                switch (mode)
                {
                    case "envcheck":
                        EnvironmentCheck.Run();
                        return 0;
                    case "sbox":
                        return SboxCheck(Need(args, 1, "credentials.json yolu"));
                    case "logon":
                        return LogonCheck(Need(args, 1, "credentials.json yolu"));
                    case "read":
                        return ReadCheck(Need(args, 1, "credentials.json yolu"), args.Length > 2 ? args[2] : null);
                    case "dbtest":
                        return DbTest(Need(args, 1, "credentials.json yolu"), args.Length > 2 ? args[2] : null);
                    case "run":
                        return MappingRun.Execute(Need(args, 1, "credentials.json yolu"));
                    case "dbreport":
                        return DbReport(Need(args, 1, "credentials.json yolu"));
                    default:
                        Console.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HATA] " + ex);
                return 2;
            }
        }

        private static int SboxCheck(string configPath)
        {
            var config = AppConfig.Load(configPath);
            Console.WriteLine("Ortam sayısı: " + config.Environments.Count);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);

            bool ok = true;
            foreach (BoEnvironment env in config.Environments)
                ok &= Try("BO  " + env.Cms, () => sbox.Get(env.Sbox));
            ok &= Try("DB", () => sbox.Get(config.DbTarget));
            return ok ? 0 : 1;
        }

        /// <summary>Her ortamda: Sbox -> logon -> UNV listesi -> logoff.</summary>
        private static int LogonCheck(string configPath)
        {
            var config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);

            bool ok = true;
            foreach (BoEnvironment env in config.Environments)
            {
                Console.WriteLine("=== " + env.Cms + " ===");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using (var session = BoSession.Logon(env.Cms, sbox.Get(env.Sbox), AuthType))
                    {
                        Console.WriteLine("[OK]   Logon başarılı");
                        var universes = UnvCatalog.ListUnv(session.InfoStore);
                        Console.WriteLine("[OK]   UNV sayısı: " + universes.Count);
                        foreach (var u in universes.Take(10))
                            Console.WriteLine("       " + u);
                    }   // <- Logoff burada, hata olsa bile
                    Console.WriteLine("[OK]   Logoff. Süre: " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception ex)
                {
                    ok = false;
                    Console.WriteLine("[HATA] " + ex);
                }
            }
            return ok ? 0 : 1;
        }

        /// <summary>İlk ortamda tek bir UNV'yi Designer ile açar, nesnelerini okur ve kapatır.</summary>
        private static int ReadCheck(string configPath, string filter)
        {
            var config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);
            BoEnvironment env = config.Environments[0];
            Credential credential = sbox.Get(env.Sbox);
            Console.WriteLine("=== " + env.Cms + " ===");

            UniverseInfo target;
            using (var session = BoSession.Logon(env.Cms, credential, AuthType))
            {
                target = UnvCatalog.ListUnv(session.InfoStore).FirstOrDefault(u =>
                    filter == null || u.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (target == null)
            {
                Console.WriteLine("[HATA] Eşleşen UNV bulunamadı.");
                return 1;
            }

            using (var designer = DesignerHost.Start())
            {
                Console.WriteLine("[OK]   Designer başladı. PID: " + designer.ProcessId);
                designer.Logon(credential, env.Cms, AuthType);
                Console.WriteLine("[OK]   Designer logon");

                Console.WriteLine("Okunuyor: " + target);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var objects = designer.Read(target);
                Console.WriteLine("[OK]   Nesne sayısı: " + objects.Count + "  Süre: " + sw.ElapsedMilliseconds + " ms");

                var byQ = objects.GroupBy(o => o.Qualification).OrderBy(g => g.Key)
                                 .Select(g => "Q" + g.Key + "=" + g.Count());
                Console.WriteLine("       Qualification dağılımı: " + string.Join(", ", byQ));
                foreach (var o in objects.Take(15))
                    Console.WriteLine("       " + o);
            }   // <- Designer Quit + gerekirse süreç sonlandırma

            int left = System.Diagnostics.Process.GetProcessesByName("designer").Length;
            Console.WriteLine((left == 0 ? "[OK]   " : "[UYARI] ") + "Kalan designer.exe: " + left);
            return 0;
        }

        /// <summary>
        /// Tek bir UNV okur ve SYSTEM='DBTEST_UNV' olarak yazar, sayar, sonra kendi satırlarını siler.
        /// Gerçek veriye dokunmaz.
        /// </summary>
        private static int DbTest(string configPath, string filter)
        {
            var config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);
            BoEnvironment env = config.Environments[0];
            Credential credential = sbox.Get(env.Sbox);

            UniverseInfo target;
            using (var session = BoSession.Logon(env.Cms, credential, AuthType))
            {
                target = UnvCatalog.ListUnv(session.InfoStore).FirstOrDefault(u =>
                    filter == null || u.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }
            if (target == null)
            {
                Console.WriteLine("[HATA] Eşleşen UNV bulunamadı.");
                return 1;
            }

            var data = new System.Collections.Generic.Dictionary<UniverseInfo, System.Collections.Generic.List<UniverseObject>>();
            using (var designer = DesignerHost.Start())
            {
                designer.Logon(credential, env.Cms, AuthType);
                data[target] = designer.Read(target);
            }
            Console.WriteLine("[OK]   Okundu: " + target + " -> " + data[target].Count + " nesne");

            var writer = new OracleMetadataWriter(config.DbJdbcUrl, sbox.Get(config.DbTarget));
            int inserted = writer.ReplaceSystem(TestSystem, DateTime.Now, data, new string[0]);
            Console.WriteLine("[OK]   Eklenen satır: " + inserted);
            Console.WriteLine("[OK]   Tablodaki " + TestSystem + " satırı: " + writer.CountUnvRows(TestSystem));

            writer.ReplaceSystem(TestSystem, DateTime.Now,
                new System.Collections.Generic.Dictionary<UniverseInfo, System.Collections.Generic.List<UniverseObject>>(),
                new string[0]);
            Console.WriteLine("[OK]   Temizlendi. Kalan " + TestSystem + " satırı: " + writer.CountUnvRows(TestSystem));
            return 0;
        }

        /// <summary>Tablonun SYSTEM / UNX-UNV / zaman bazında özeti (salt okuma).</summary>
        private static int DbReport(string configPath)
        {
            var config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);
            var writer = new OracleMetadataWriter(config.DbJdbcUrl, sbox.Get(config.DbTarget));

            Console.WriteLine(string.Format("{0,-30} {1,-4} {2,-17} {3,10} {4,10}", "SYSTEM", "TUR", "ZAMAN", "SATIR", "UNIVERSE"));
            Console.WriteLine(new string('-', 75));
            foreach (var r in writer.Report())
                Console.WriteLine(string.Format("{0,-30} {1,-4} {2,-17} {3,10} {4,10}", r[0], r[1], r[2], r[3], r[4]));
            return 0;
        }

        private static bool Try(string label, Func<Credential> get)
        {
            try
            {
                Console.WriteLine("[OK]   " + label + " -> " + get());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HATA] " + label + " -> " + ex.Message);
                if (ex.InnerException != null) Console.WriteLine("       sebep: " + ex.InnerException.Message);
                return false;
            }
        }

        private static string Need(string[] args, int i, string name)
        {
            if (args.Length <= i) throw new ArgumentException("Eksik parametre: " + name);
            return args[i];
        }
    }
}
