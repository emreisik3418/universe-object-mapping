using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    /// - Okuma döngüsü (yeniden başlatma, zaman aşımı, hata yönetimi): UniverseBatchReader.
    /// - Her universe okununca kendi satırları hemen yenilenir (UnvDbWriter); bellekte veri birikmez.
    /// - Hata veren universe'lerin eski satırları DB'de korunur; sonra "retry" moduyla tekrar okunabilir.
    /// - Sonda CMS'te artık olmayan universe'lerin satırları silinir (en az bir universe okunduysa).
    /// - Bir ortamda hiç UNV listelenemezse o ortam için DB'ye DOKUNULMAZ.
    /// - Çıkış kodu: 0 = her şey başarılı, 1 = bazı universe/ortamlarda hata, 2 = uygulama başlayamadı.
    /// </summary>
    internal static class MappingRun
    {
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

            // DB'yi en başta kontrol et: sorun varsa saatlerce okuma yaptıktan sonra değil, hemen öğrenelim
            var db = new UnvDbWriter(config.DbJdbcUrl, sbox.Get(config.DbTarget));
            db.Ping();

            DateTime exportDate = DateTime.Now;
            bool allOk = true;

            foreach (BoEnvironment env in config.Environments)
            {
                try
                {
                    allOk &= ProcessEnvironment(env, sbox, db, exportDate, log);
                }
                catch (Exception ex)
                {
                    log.Error("[" + env.Cms + "] Ortam işlenemedi", ex);
                    allOk = false;
                }
            }

            log.Info("Bitti. Süre: " + (int)total.Elapsed.TotalMinutes + " dk. Sonuç: " +
                     (allOk ? "BAŞARILI" : "HATALI (detaylar logda)"));
            return allOk ? 0 : 1;
        }

        /// <returns>Ortamdaki her şey sorunsuzsa true</returns>
        private static bool ProcessEnvironment(BoEnvironment env, SboxCredentialProvider sbox, UnvDbWriter db,
                                               DateTime exportDate, RunLog log)
        {
            string cms = env.Cms;
            var sw = Stopwatch.StartNew();
            Credential credential = sbox.Get(env.Sbox);

            List<UniverseInfo> universes = UniverseBatchReader.WithRetry(cms + " CMS logon", log, () =>
            {
                using (var session = BoSession.Logon(cms, credential, UniverseBatchReader.AuthType))
                {
                    return UnvCatalog.ListUnv(session.InfoStore);
                }   // <- Logoff; Designer kendi oturumunu açar
            });
            log.Info("[" + cms + "] Logon OK. UNV sayısı: " + universes.Count);

            if (universes.Count == 0)
            {
                log.Warn("[" + cms + "] Hiç UNV listelenmedi. Güvenlik için DB'ye dokunulmadı.");
                return false;
            }

            BatchResult r = UniverseBatchReader.Read(universes, credential, cms, log,
                (u, objects, tables) => db.ReplaceUniverse(cms, exportDate, u, objects, tables));

            log.Info(string.Format("[{0}] Object mapping yazıldı: {1} satır ({2}/{3} UNV, {4} nesne, hatalı {5})",
                cms, r.ObjectRows, r.OkCount, universes.Count, r.ObjectCount, r.FailedCuids.Count));
            log.Info(string.Format("[{0}] Table mapping yazıldı: {1} satır ({2} UNV, {3} tablo, hatalı {4})",
                cms, r.TableRows, r.OkCount - (r.FailedTableCuids.Count - r.FailedCuids.Count), r.TableCount, r.FailedTableCuids.Count));

            bool ok = r.FailedCuids.Count == 0 && r.FailedTableCuids.Count == 0;

            if (r.OkCount == 0)
            {
                log.Error("[" + cms + "] Hiçbir UNV okunamadı. Silinmiş universe temizliği yapılmadı.");
                return false;
            }

            try
            {
                WriteCount removed = db.RemoveDeleted(cms, universes.Select(u => u.Cuid).ToList());
                log.Info(string.Format("[{0}] CMS'te olmayan universe satırları silindi: OM {1}, TM {2}",
                    cms, removed.ObjectRows, removed.TableRows));
            }
            catch (Exception ex)
            {
                ok = false;
                log.Error("[" + cms + "] Silinmiş universe temizliği yapılamadı (bir sonraki run'da tekrar denenir)", ex);
            }

            log.Info("[" + cms + "] Süre: " + (int)sw.Elapsed.TotalSeconds + " sn");
            return ok;
        }
    }
}
