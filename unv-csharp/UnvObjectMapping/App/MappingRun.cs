using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnvObjectMapping.Config;
using UnvObjectMapping.Credentials;
using UnvObjectMapping.Model;
using UnvObjectMapping.Output;
using UnvObjectMapping.Sdk;

namespace UnvObjectMapping.App
{
    /// <summary>
    /// Ana akış: tüm ortamlar, tüm UNV'ler.
    ///
    /// - Hata veren universe loglanır ve atlanır; eski satırları DB'de korunur.
    /// - Designer her RestartEvery universe'te bir ve HER HATADAN SONRA yeniden başlatılır
    ///   (bellek şişmesin, bozuk durumda kalmasın).
    /// - Bir universe UniverseTimeout süresini aşarsa Designer süreci sonlandırılır (donma koruması).
    /// - Bir ortamda hiç UNV listelenemezse veya hiçbiri okunamazsa o ortam için DB'ye DOKUNULMAZ.
    /// - Çıkış kodu: 0 = her şey başarılı, 1 = bazı universe/ortamlarda hata, 2 = uygulama başlayamadı.
    /// </summary>
    internal static class MappingRun
    {
        private const string AuthType = "secEnterprise";
        private const int RestartEvery = 50;
        private static readonly TimeSpan UniverseTimeout = TimeSpan.FromMinutes(10);

        public static int Execute(string configPath)
        {
            RunLog log = null;
            try
            {
                log = RunLog.Open(Path.Combine(Environment.CurrentDirectory, "logs"));
                return Run(configPath, log);
            }
            catch (Exception ex)
            {
                if (log != null) log.Error("Beklenmeyen hata, uygulama durduruldu", ex);
                else Console.WriteLine("[HATA] " + ex);
                return 2;
            }
            finally
            {
                log?.Dispose();
            }
        }

        private static int Run(string configPath, RunLog log)
        {
            var total = Stopwatch.StartNew();
            log.Info("Başladı. Log: " + log.Path);

            AppConfig config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);

            // DB kimlik bilgisini en başta al: sorun varsa saatlerce okuma yaptıktan sonra değil, hemen öğrenelim
            var writer = new OracleMetadataWriter(config.DbJdbcUrl, sbox.Get(config.DbTarget));

            DateTime exportDate = DateTime.Now;
            bool allOk = true;

            foreach (BoEnvironment env in config.Environments)
            {
                try
                {
                    allOk &= ProcessEnvironment(env, sbox, writer, exportDate, log);
                }
                catch (Exception ex)
                {
                    log.Error("[" + env.Cms + "] Ortam işlenemedi, DB'ye yazılmadı", ex);
                    allOk = false;
                }
            }

            log.Info("Bitti. Süre: " + (int)total.Elapsed.TotalMinutes + " dk. Sonuç: " +
                     (allOk ? "BAŞARILI" : "HATALI (detaylar logda)"));
            return allOk ? 0 : 1;
        }

        /// <returns>Ortamdaki her şey sorunsuzsa true</returns>
        private static bool ProcessEnvironment(BoEnvironment env, SboxCredentialProvider sbox,
                                               OracleMetadataWriter writer, DateTime exportDate, RunLog log)
        {
            string cms = env.Cms;
            var sw = Stopwatch.StartNew();
            Credential credential = sbox.Get(env.Sbox);

            List<UniverseInfo> universes;
            using (var session = BoSession.Logon(cms, credential, AuthType))
            {
                universes = UnvCatalog.ListUnv(session.InfoStore);
            }   // <- Logoff; Designer kendi oturumunu açar
            log.Info("[" + cms + "] Logon OK. UNV sayısı: " + universes.Count);

            if (universes.Count == 0)
            {
                log.Warn("[" + cms + "] Hiç UNV listelenmedi. Güvenlik için DB'ye dokunulmadı.");
                return false;
            }

            var data = new Dictionary<UniverseInfo, List<UniverseObject>>();
            var failedCuids = new List<string>();
            int objectCount = 0;

            DesignerHost designer = null;
            int sinceStart = 0;
            try
            {
                for (int i = 0; i < universes.Count; i++)
                {
                    UniverseInfo u = universes[i];
                    string label = string.Format("[{0}] {1}/{2}", cms, i + 1, universes.Count);

                    if (designer == null || sinceStart >= RestartEvery)
                    {
                        designer?.Dispose();
                        designer = null;
                        designer = DesignerHost.Start();
                        designer.Logon(credential, cms, AuthType);
                        sinceStart = 0;
                    }
                    sinceStart++;

                    bool timedOut = false;
                    DesignerHost current = designer;
                    using (new Timer(_ => { timedOut = true; current.Kill(); },
                                     null, UniverseTimeout, Timeout.InfiniteTimeSpan))
                    {
                        try
                        {
                            List<UniverseObject> objects = designer.Read(u);
                            data[u] = objects;
                            objectCount += objects.Count;
                            log.Info(label + " OK    " + u + " (" + objects.Count + " nesne)");
                        }
                        catch (Exception ex)
                        {
                            failedCuids.Add(u.Cuid);
                            log.Error(label + " HATA  " + u + (timedOut ? " (zaman aşımı, Designer sonlandırıldı)" : ""), ex);

                            // Hatadan sonra Designer'ın durumuna güvenme: kapat, sıradaki universe'te yeniden başlat
                            designer.Dispose();
                            designer = null;
                        }
                    }
                }
            }
            finally
            {
                designer?.Dispose();
            }

            if (data.Count == 0)
            {
                log.Error("[" + cms + "] Hiçbir UNV okunamadı. Güvenlik için DB'ye dokunulmadı.");
                return false;
            }

            int inserted = writer.ReplaceSystem(cms, exportDate, data, failedCuids);
            log.Info(string.Format("[{0}] DB'ye yazıldı: {1} satır. Okunan: {2}/{3} UNV, {4} nesne. Hatalı: {5}. Süre: {6} sn",
                cms, inserted, data.Count, universes.Count, objectCount, failedCuids.Count, (int)sw.Elapsed.TotalSeconds));

            return failedCuids.Count == 0;
        }
    }
}
