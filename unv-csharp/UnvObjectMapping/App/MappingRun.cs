using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// - Hata veren universe'lerin eski satırları DB'de korunur; sonra "retry" moduyla tekrar okunabilir.
    /// - Bir ortamda hiç UNV listelenemezse veya hiçbiri okunamazsa o ortam için DB'ye DOKUNULMAZ.
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

            // DB kimlik bilgisini en başta al: sorun varsa saatlerce okuma yaptıktan sonra değil, hemen öğrenelim
            Credential dbCredential = sbox.Get(config.DbTarget);
            var writer = new OracleMetadataWriter(config.DbJdbcUrl, dbCredential);
            var tableWriter = new OracleTableMappingWriter(config.DbJdbcUrl, dbCredential);

            DateTime exportDate = DateTime.Now;
            bool allOk = true;

            foreach (BoEnvironment env in config.Environments)
            {
                try
                {
                    allOk &= ProcessEnvironment(env, sbox, writer, tableWriter, exportDate, log);
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
                                               OracleMetadataWriter writer, OracleTableMappingWriter tableWriter,
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

            BatchResult r = UniverseBatchReader.Read(universes, credential, cms, log);
            var data = r.Objects;
            var tableData = r.Tables;
            var failedCuids = r.FailedCuids;
            var failedTableCuids = r.FailedTableCuids;
            int objectCount = r.ObjectCount;
            int tableCount = r.TableCount;

            if (data.Count == 0)
            {
                log.Error("[" + cms + "] Hiçbir UNV okunamadı. Güvenlik için DB'ye dokunulmadı.");
                return false;
            }

            bool ok = failedCuids.Count == 0 && failedTableCuids.Count == 0;

            // Object mapping ve table mapping ayrı transaction: birinin hatası diğerini engellemez
            try
            {
                int inserted = writer.ReplaceSystem(cms, exportDate, data, failedCuids);
                log.Info(string.Format("[{0}] Object mapping yazıldı: {1} satır ({2}/{3} UNV, {4} nesne, hatalı {5})",
                    cms, inserted, data.Count, universes.Count, objectCount, failedCuids.Count));
            }
            catch (Exception ex)
            {
                ok = false;
                log.Error("[" + cms + "] Object mapping DB'ye yazılamadı (eski veri korundu)", ex);
            }

            if (tableData.Count == 0)
            {
                ok = false;
                log.Error("[" + cms + "] Hiçbir UNV'nin tabloları okunamadı. Table mapping'e dokunulmadı.");
            }
            else
            {
                try
                {
                    int inserted = tableWriter.ReplaceSystem(cms, exportDate, tableData, failedTableCuids);
                    log.Info(string.Format("[{0}] Table mapping yazıldı: {1} satır ({2} UNV, {3} tablo, hatalı {4})",
                        cms, inserted, tableData.Count, tableCount, failedTableCuids.Count));
                }
                catch (Exception ex)
                {
                    ok = false;
                    log.Error("[" + cms + "] Table mapping DB'ye yazılamadı (eski veri korundu)", ex);
                }
            }

            log.Info("[" + cms + "] Süre: " + (int)sw.Elapsed.TotalSeconds + " sn");
            return ok;
        }
    }
}
