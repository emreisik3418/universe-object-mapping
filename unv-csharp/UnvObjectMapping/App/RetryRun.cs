using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnvObjectMapping.Config;
using UnvObjectMapping.Credentials;
using UnvObjectMapping.Model;
using UnvObjectMapping.Output;
using UnvObjectMapping.Sdk;

namespace UnvObjectMapping.App
{
    /// <summary>
    /// "retry" modu: önceki bir run (veya retry) log'unda hatalı görünen UNV'leri tekrar okur.
    ///
    /// - Log'dan HATA / ATLANDI / "Tablolar okunamadı" satırlarındaki [SYSTEM] ve [CUID] alınır.
    /// - Sadece bu universe'ler okunur; DB'de SADECE başarıyla okunanların satırları silinip yeniden yazılır.
    ///   Sistemin diğer universe'lerine ve UNX satırlarına dokunulmaz.
    /// - Yine hata alanların eski satırları korunur. Kendi log'u (logs\unv_retry_*.log) aynı formatta
    ///   olduğu için gerekirse retry log'u ile tekrar retry yapılabilir.
    /// </summary>
    internal static class RetryRun
    {
        // "2026-10-16 17:02:11 ERROR [HOST:6400] 12/489 HATA  KLASOR\Ad.unv [CUID] : ..."
        private static readonly Regex FailedLine = new Regex(
            @"^\S+ \S+ (?:ERROR|WARN)\s+\[(?<cms>[^\]]+)\] \d+/\d+ (?:HATA  |ATLANDI |Tablolar okunamadı)",
            RegexOptions.Compiled);

        // Universe'ün ToString'indeki " [CUID]"; arkasından " (zaman aşımı...", " : hata" veya satır sonu gelir
        private static readonly Regex CuidPart = new Regex(
            @"\[(?<cuid>[^\[\]\s]+)\](?= \(zaman| :|\s*$)", RegexOptions.Compiled);

        public static int Execute(string configPath, string logPath)
        {
            RunLog log = null;
            try
            {
                string logsDir = Path.Combine(Environment.CurrentDirectory, "logs");
                if (string.IsNullOrEmpty(logPath))
                {
                    logPath = FindLatestLog(logsDir);
                    if (logPath == null)
                    {
                        Console.WriteLine("[HATA] " + logsDir + " altında log bulunamadı. Log dosyasını parametre olarak verin.");
                        return 2;
                    }
                }
                if (!File.Exists(logPath))
                {
                    Console.WriteLine("[HATA] Log dosyası yok: " + logPath);
                    return 2;
                }

                log = RunLog.Open(logsDir, "unv_retry");
                return Run(configPath, logPath, log);
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

        private static int Run(string configPath, string sourceLog, RunLog log)
        {
            var total = Stopwatch.StartNew();
            log.Info("Retry başladı. Kaynak log: " + sourceLog + "  Log: " + log.Path);

            Dictionary<string, HashSet<string>> failed = ParseFailed(sourceLog, log);
            if (failed.Count == 0)
            {
                log.Info("Kaynak log'da hatalı UNV yok. Yapılacak iş yok.");
                return 0;
            }
            foreach (var kv in failed)
                log.Info("[" + kv.Key + "] Log'da hatalı UNV: " + kv.Value.Count);

            AppConfig config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);
            Credential dbCredential = sbox.Get(config.DbTarget);
            var writer = new OracleMetadataWriter(config.DbJdbcUrl, dbCredential);
            var tableWriter = new OracleTableMappingWriter(config.DbJdbcUrl, dbCredential);

            DateTime exportDate = DateTime.Now;
            bool allOk = true;

            foreach (var kv in failed)
            {
                BoEnvironment env = config.Environments.FirstOrDefault(e =>
                    string.Equals(e.Cms, kv.Key, StringComparison.OrdinalIgnoreCase));
                if (env == null)
                {
                    log.Error("[" + kv.Key + "] credentials.json'da bu ortam yok, atlandı.");
                    allOk = false;
                    continue;
                }

                try
                {
                    allOk &= ProcessEnvironment(env, kv.Value, sbox, writer, tableWriter, exportDate, log);
                }
                catch (Exception ex)
                {
                    log.Error("[" + env.Cms + "] Ortam işlenemedi, DB'ye yazılmadı", ex);
                    allOk = false;
                }
            }

            log.Info("Retry bitti. Süre: " + (int)total.Elapsed.TotalMinutes + " dk. Sonuç: " +
                     (allOk ? "BAŞARILI" : "HATALI (kalanlar için bu log ile tekrar retry yapılabilir)"));
            return allOk ? 0 : 1;
        }

        private static bool ProcessEnvironment(BoEnvironment env, HashSet<string> cuids, SboxCredentialProvider sbox,
                                               OracleMetadataWriter writer, OracleTableMappingWriter tableWriter,
                                               DateTime exportDate, RunLog log)
        {
            string cms = env.Cms;
            var sw = Stopwatch.StartNew();
            Credential credential = sbox.Get(env.Sbox);

            List<UniverseInfo> all = UniverseBatchReader.WithRetry(cms + " CMS logon", log, () =>
            {
                using (var session = BoSession.Logon(cms, credential, UniverseBatchReader.AuthType))
                {
                    return UnvCatalog.ListUnv(session.InfoStore);
                }
            });

            // CMS'teki güncel bilgiyle (ad/klasör değişmiş olabilir) ve CMS sırasıyla
            List<UniverseInfo> targets = all.Where(u => u.Cuid != null && cuids.Contains(u.Cuid)).ToList();
            var gone = cuids.Except(targets.Select(u => u.Cuid)).ToList();
            foreach (string c in gone)
                log.Warn("[" + cms + "] CMS'te artık yok, atlandı: " + c);
            log.Info("[" + cms + "] Logon OK. Tekrar okunacak UNV: " + targets.Count);

            if (targets.Count == 0)
                return gone.Count == 0;

            BatchResult r = UniverseBatchReader.Read(targets, credential, cms, log);
            bool ok = r.FailedCuids.Count == 0 && r.FailedTableCuids.Count == 0;

            try
            {
                int inserted = writer.ReplaceUniverses(cms, exportDate, r.Objects);
                log.Info(string.Format("[{0}] Object mapping yazıldı: {1} satır ({2}/{3} UNV, {4} nesne, yine hatalı {5})",
                    cms, inserted, r.Objects.Count, targets.Count, r.ObjectCount, r.FailedCuids.Count));
            }
            catch (Exception ex)
            {
                ok = false;
                log.Error("[" + cms + "] Object mapping DB'ye yazılamadı (eski veri korundu)", ex);
            }

            try
            {
                int inserted = tableWriter.ReplaceUniverses(cms, exportDate, r.Tables);
                log.Info(string.Format("[{0}] Table mapping yazıldı: {1} satır ({2}/{3} UNV, {4} tablo, yine hatalı {5})",
                    cms, inserted, r.Tables.Count, targets.Count, r.TableCount, r.FailedTableCuids.Count));
            }
            catch (Exception ex)
            {
                ok = false;
                log.Error("[" + cms + "] Table mapping DB'ye yazılamadı (eski veri korundu)", ex);
            }

            log.Info("[" + cms + "] Süre: " + (int)sw.Elapsed.TotalSeconds + " sn");
            return ok;
        }

        /// <summary>Log'daki hatalı UNV'ler: SYSTEM -> CUID kümesi.</summary>
        internal static Dictionary<string, HashSet<string>> ParseFailed(string path, RunLog log)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            bool listsSkipped = lines.Any(l => l.Contains(" ATLANDI "));

            foreach (string line in lines)
            {
                // Bu durumlarda tek tek universe listesi yok; retry kapsayamaz, uyar
                if (line.Contains("Ortam işlenemedi") || line.Contains("Hiçbir UNV okunamadı") ||
                    line.Contains("Hiç UNV listelenmedi") ||
                    (line.Contains("Designer açılamadı, kalan") && !listsSkipped))
                {
                    log.Warn("Kaynak log'daki bu ortam hatası retry ile kapsanamaz, o ortam için tam 'run' gerekir: " + line);
                    continue;
                }

                Match m = FailedLine.Match(line);
                if (!m.Success) continue;
                Match c = CuidPart.Match(line, m.Length);
                if (!c.Success)
                {
                    log.Warn("CUID okunamadı, satır atlandı: " + line);
                    continue;
                }

                string cms = m.Groups["cms"].Value;
                HashSet<string> set;
                if (!result.TryGetValue(cms, out set))
                    result[cms] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(c.Groups["cuid"].Value);
            }
            return result;
        }

        private static string FindLatestLog(string logsDir)
        {
            if (!Directory.Exists(logsDir)) return null;
            return new DirectoryInfo(logsDir).GetFiles("unv_*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
    }
}
