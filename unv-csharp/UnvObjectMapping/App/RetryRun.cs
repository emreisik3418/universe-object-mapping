using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>Kaynak log'dan çıkarılan "tekrar okunacaklar".</summary>
    internal sealed class FailedSet
    {
        /// <summary>SYSTEM -> log'da tek tek hatalı görünen CUID'ler.</summary>
        public readonly Dictionary<string, HashSet<string>> Cuids =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Log'da ortam düzeyinde hata olan (hangi universe'lerin eksik kaldığı log'dan anlaşılmayan) SYSTEM'ler.
        /// Bunlar için eksikler DB'den bulunur: log başlangıcından beri yazılmamış her UNV.
        /// </summary>
        public readonly HashSet<string> NeedsDbCheck = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public DateTime? LogStart;

        public IEnumerable<string> Systems => Cuids.Keys.Union(NeedsDbCheck, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "retry" modu: önceki bir run (veya retry) log'unda hatalı görünen UNV'leri tekrar okur.
    ///
    /// - Log'dan HATA / ATLANDI / "Tablolar okunamadı" satırlarındaki [SYSTEM] ve [CUID] alınır.
    /// - Ortam düzeyindeki hatalarda (ör. eski sürümün "Designer açılamadı, kalan N UNV atlandı" satırı)
    ///   eksikler DB'den bulunur: CMS'te olup log başlangıcından beri object mapping'e yazılmamış UNV'ler.
    /// - Sadece bu universe'ler okunur ve sadece onların satırları yenilenir.
    ///   Sistemin diğer universe'lerine ve UNX satırlarına dokunulmaz, silinmiş universe temizliği yapılmaz.
    /// - Yine hata alanların eski satırları korunur. Kendi log'u (logs\unv_retry_*.log) aynı formatta
    ///   olduğu için gerekirse retry log'u ile tekrar retry yapılabilir.
    /// </summary>
    internal static class RetryRun
    {
        // "2026-10-16 17:02:11 ERROR [HOST:6400] 12/489 HATA  KLASOR\Ad.unv [CUID] : ..."
        private static readonly Regex FailedLine = new Regex(
            @"^\S+ \S+ (?:ERROR|WARN)\s+\[(?<cms>[^\]]+)\] \d+/\d+ (?:HATA  |ATLANDI |Tablolar okunamadı)",
            RegexOptions.Compiled);

        // Universe'ün ToString'indeki " [CUID]"; arkasından " (...", " : hata" veya satır sonu gelir
        private static readonly Regex CuidPart = new Regex(
            @"\[(?<cuid>[^\[\]\s]+)\](?= \(| :|\s*$)", RegexOptions.Compiled);

        // Ortam düzeyinde hata: "... ERROR [HOST:6400] ..." + aşağıdaki ifadelerden biri
        private static readonly Regex EnvLine = new Regex(
            @"^\S+ \S+ (?:ERROR|WARN)\s+\[(?<cms>[^\]]+)\]", RegexOptions.Compiled);

        private static readonly string[] EnvMarkers =
        {
            "Designer açılamadı, kalan", "Ortam işlenemedi", "Hiçbir UNV okunamadı", "Hiç UNV listelenmedi",
            "DB'ye yazılamadı", "DB'ye üst üste"
        };

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
                return Run(configPath, Path.GetFullPath(logPath), log);
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

            FailedSet failed = ParseFailed(sourceLog, log);
            List<string> systems = failed.Systems.ToList();
            if (systems.Count == 0)
            {
                log.Info("Kaynak log'da hatalı UNV yok. Yapılacak iş yok.");
                return 0;
            }
            foreach (string sys in systems)
            {
                HashSet<string> set;
                int n = failed.Cuids.TryGetValue(sys, out set) ? set.Count : 0;
                log.Info("[" + sys + "] Log'da tek tek hatalı UNV: " + n +
                         (failed.NeedsDbCheck.Contains(sys) ? "  + ortam düzeyinde hata var, eksikler DB'den bulunacak" : ""));
            }
            if (failed.NeedsDbCheck.Count > 0 && failed.LogStart == null)
            {
                log.Error("Kaynak log'un başlangıç zamanı okunamadı; DB'den eksik tespiti yapılamaz.");
                return 2;
            }

            AppConfig config = AppConfig.Load(configPath);
            var sbox = new SboxCredentialProvider(config.SboxEndpoint, config.SboxUsername, config.SboxPassword);
            var db = new UnvDbWriter(config.DbJdbcUrl, sbox.Get(config.DbTarget));
            db.Ping();

            DateTime exportDate = DateTime.Now;
            bool allOk = true;

            foreach (string sys in systems)
            {
                BoEnvironment env = config.Environments.FirstOrDefault(e =>
                    string.Equals(e.Cms, sys, StringComparison.OrdinalIgnoreCase));
                if (env == null)
                {
                    log.Error("[" + sys + "] credentials.json'da bu ortam yok, atlandı.");
                    allOk = false;
                    continue;
                }

                try
                {
                    allOk &= ProcessEnvironment(env, failed, sbox, db, exportDate, log);
                }
                catch (Exception ex)
                {
                    log.Error("[" + env.Cms + "] Ortam işlenemedi", ex);
                    allOk = false;
                }
            }

            log.Info("Retry bitti. Süre: " + (int)total.Elapsed.TotalMinutes + " dk. Sonuç: " +
                     (allOk ? "BAŞARILI" : "HATALI (kalanlar için bu log ile tekrar retry yapılabilir)"));
            return allOk ? 0 : 1;
        }

        private static bool ProcessEnvironment(BoEnvironment env, FailedSet failed, SboxCredentialProvider sbox,
                                               UnvDbWriter db, DateTime exportDate, RunLog log)
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

            HashSet<string> cuids;
            if (!failed.Cuids.TryGetValue(cms, out cuids))
                cuids = new HashSet<string>(StringComparer.Ordinal);

            // Ortam düzeyinde hata: log başlangıcından beri DB'ye yazılmamış her UNV de tekrar okunur
            HashSet<string> written = null;
            if (failed.NeedsDbCheck.Contains(cms))
            {
                written = db.CuidsWrittenSince(cms, failed.LogStart.Value);
                log.Info("[" + cms + "] " + failed.LogStart.Value.ToString("yyyy-MM-dd HH:mm:ss") +
                         " sonrası DB'ye yazılmış UNV: " + written.Count + " / CMS'teki UNV: " + all.Count);
            }

            // CMS'teki güncel bilgiyle (ad/klasör değişmiş olabilir) ve CMS sırasıyla
            List<UniverseInfo> targets = all.Where(u => u.Cuid != null &&
                (cuids.Contains(u.Cuid) || (written != null && !written.Contains(u.Cuid)))).ToList();
            var gone = cuids.Except(all.Select(u => u.Cuid)).ToList();
            foreach (string c in gone)
                log.Warn("[" + cms + "] CMS'te artık yok, atlandı: " + c);
            log.Info("[" + cms + "] Logon OK. Tekrar okunacak UNV: " + targets.Count);

            if (targets.Count == 0)
                return true;

            BatchResult r = UniverseBatchReader.Read(targets, credential, cms, log,
                (u, objects, tables) => db.ReplaceUniverse(cms, exportDate, u, objects, tables));

            log.Info(string.Format("[{0}] Object mapping yazıldı: {1} satır ({2}/{3} UNV, {4} nesne, yine hatalı {5})",
                cms, r.ObjectRows, r.OkCount, targets.Count, r.ObjectCount, r.FailedCuids.Count));
            log.Info(string.Format("[{0}] Table mapping yazıldı: {1} satır ({2} tablo, yine hatalı {3})",
                cms, r.TableRows, r.TableCount, r.FailedTableCuids.Count));
            log.Info("[" + cms + "] Süre: " + (int)sw.Elapsed.TotalSeconds + " sn");
            return r.FailedCuids.Count == 0 && r.FailedTableCuids.Count == 0;
        }

        /// <summary>Log'daki hatalı UNV'ler ve ortam düzeyindeki hatalar.</summary>
        internal static FailedSet ParseFailed(string path, RunLog log)
        {
            var result = new FailedSet();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);

            if (lines.Length > 0 && lines[0].Length >= 19)
            {
                DateTime start;
                if (DateTime.TryParseExact(lines[0].Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                                           CultureInfo.InvariantCulture, DateTimeStyles.None, out start))
                    result.LogStart = start;
            }

            // Yeni sürüm "Designer açılamadı"dan sonra kalanları tek tek ATLANDI olarak yazar; eski sürüm yazmazdı
            var listsSkipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                Match m = FailedLine.Match(line);
                if (m.Success && line.IndexOf(" ATLANDI ", m.Index, StringComparison.Ordinal) > 0)
                    listsSkipped.Add(m.Groups["cms"].Value);
            }

            foreach (string line in lines)
            {
                Match m = FailedLine.Match(line);
                if (m.Success)
                {
                    Match c = CuidPart.Match(line, m.Length);
                    if (!c.Success)
                    {
                        log.Warn("CUID okunamadı, satır atlandı: " + line);
                        continue;
                    }
                    string cms = m.Groups["cms"].Value;
                    HashSet<string> set;
                    if (!result.Cuids.TryGetValue(cms, out set))
                        result.Cuids[cms] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(c.Groups["cuid"].Value);
                    continue;
                }

                Match e = EnvLine.Match(line);
                if (!e.Success || !EnvMarkers.Any(k => line.Contains(k))) continue;
                string envCms = e.Groups["cms"].Value;
                if (line.Contains("Designer açılamadı, kalan") && listsSkipped.Contains(envCms)) continue;
                if (line.Contains("DB'ye üst üste") && listsSkipped.Contains(envCms)) continue;
                result.NeedsDbCheck.Add(envCms);
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
