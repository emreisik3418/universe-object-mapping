using System;
using System.Collections.Generic;
using System.Threading;
using UnvObjectMapping.Model;
using UnvObjectMapping.Output;
using UnvObjectMapping.Sdk;

namespace UnvObjectMapping.App
{
    /// <summary>Bir ortamdaki universe listesini okuyup yazmanın özeti (veri bellekte TUTULMAZ).</summary>
    internal sealed class BatchResult
    {
        public readonly List<string> FailedCuids = new List<string>();
        public readonly List<string> FailedTableCuids = new List<string>();
        public int OkCount;
        public int ObjectCount;
        public int TableCount;
        public int ObjectRows;
        public int TableRows;
    }

    /// <summary>
    /// "run" ve "retry" modlarının ortak okuma döngüsü.
    ///
    /// - Her universe okunur okunmaz persist ile DB'ye yazılır ve bellekten atılır
    ///   (tüm ortamı bellekte toplamak 32-bit süreçte OutOfMemoryException veriyordu).
    /// - Hata veren universe loglanır ve atlanır; eski satırları DB'de kalır.
    /// - Designer her RestartEvery universe'te bir ve HER HATADAN SONRA yeniden başlatılır.
    /// - Bir universe UniverseTimeout süresini aşarsa Designer süreci sonlandırılır (donma koruması).
    /// - Designer hiç açılamazsa veya DB üst üste yazılamazsa kalan universe'ler tek tek "ATLANDI" loglanır.
    ///
    /// Log satır formatı ("HATA  ", "ATLANDI", "Tablolar okunamadı" + " [CUID]") retry modunun
    /// log'dan hatalıları bulması için kullanılır; değiştirirken RetryRun.ParseFailed'ı da güncelleyin.
    /// </summary>
    internal static class UniverseBatchReader
    {
        public const string AuthType = "secEnterprise";
        private const int RestartEvery = 50;
        private const int MaxConsecutiveDbErrors = 5;

        // Logon (CMS veya Designer) başarısız olursa: ör. "580 Concurrent Access Licenses are in use"
        private const int LogonAttempts = 4;
        private static readonly TimeSpan LogonRetryWait = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan UniverseTimeout = TimeSpan.FromMinutes(10);

        public delegate WriteCount Persist(UniverseInfo u, List<UniverseObject> objects, List<UniverseTable> tablesOrNull);

        public static BatchResult Read(IList<UniverseInfo> universes, Credential credential, string cms,
                                       RunLog log, Persist persist)
        {
            var result = new BatchResult();
            DesignerHost designer = null;
            int sinceStart = 0;
            int dbErrors = 0;
            try
            {
                for (int i = 0; i < universes.Count; i++)
                {
                    UniverseInfo u = universes[i];
                    string label = string.Format("[{0}] {1}/{2}", cms, i + 1, universes.Count);

                    if (designer == null || sinceStart >= RestartEvery)
                    {
                        SafeDispose(designer, log);
                        designer = null;
                        try
                        {
                            designer = WithRetry(cms + " Designer başlatma/logon", log, () => StartDesigner(credential, cms, log));
                        }
                        catch (Exception ex)
                        {
                            log.Error(label + " Designer açılamadı, kalan " + (universes.Count - i) +
                                      " UNV atlandı (eski satırları korunacak)", ex);
                            SkipRemaining(universes, i, cms, result, log);
                            break;
                        }
                        sinceStart = 0;
                    }
                    sinceStart++;

                    // 1) Oku (zaman aşımı korumalı)
                    UniverseContent content;
                    bool timedOut = false;
                    DesignerHost current = designer;
                    using (new Timer(_ => { timedOut = true; current.Kill(); },
                                     null, UniverseTimeout, Timeout.InfiniteTimeSpan))
                    {
                        try
                        {
                            content = designer.Read(u);
                        }
                        catch (Exception ex)
                        {
                            result.FailedCuids.Add(u.Cuid);
                            result.FailedTableCuids.Add(u.Cuid);
                            log.Error(label + " HATA  " + u + (timedOut ? " (zaman aşımı, Designer sonlandırıldı)" : ""), ex);

                            // Hatadan sonra Designer'ın durumuna güvenme: kapat, sıradaki universe'te yeniden başlat
                            SafeDispose(designer, log);
                            designer = null;
                            continue;
                        }
                    }

                    // 2) Hemen yaz, bellekte tutma
                    List<UniverseTable> tables = content.TableError == null ? content.Tables : null;
                    try
                    {
                        WriteCount w = persist(u, content.Objects, tables);
                        dbErrors = 0;
                        result.OkCount++;
                        result.ObjectCount += content.Objects.Count;
                        result.ObjectRows += w.ObjectRows;
                        result.TableRows += w.TableRows;

                        if (tables == null)
                        {
                            result.FailedTableCuids.Add(u.Cuid);
                            log.Warn(label + " Tablolar okunamadı (nesneler OK) " + u + " : " + RunLog.Describe(content.TableError));
                        }
                        else
                        {
                            result.TableCount += tables.Count;
                        }
                        log.Info(label + " OK    " + u + " (" + content.Objects.Count + " nesne, " + content.Tables.Count + " tablo)");
                    }
                    catch (Exception ex)
                    {
                        result.FailedCuids.Add(u.Cuid);
                        result.FailedTableCuids.Add(u.Cuid);
                        log.Error(label + " HATA  " + u + " (DB'ye yazılamadı, eski satırları korundu)", ex);

                        if (++dbErrors >= MaxConsecutiveDbErrors && i + 1 < universes.Count)
                        {
                            log.Error("[" + cms + "] DB'ye üst üste " + dbErrors + " kez yazılamadı, kalan " +
                                      (universes.Count - i - 1) + " UNV atlandı (eski satırları korunacak)");
                            SkipRemaining(universes, i + 1, cms, result, log);
                            break;
                        }
                    }
                }
            }
            finally
            {
                SafeDispose(designer, log);
            }
            return result;
        }

        /// <summary>Kalan universe'leri hatalı say ve her birini ayrı satırda logla (retry bulabilsin).</summary>
        private static void SkipRemaining(IList<UniverseInfo> universes, int from, string cms, BatchResult result, RunLog log)
        {
            for (int k = from; k < universes.Count; k++)
            {
                result.FailedCuids.Add(universes[k].Cuid);
                result.FailedTableCuids.Add(universes[k].Cuid);
                log.Error(string.Format("[{0}] {1}/{2} ATLANDI {3}", cms, k + 1, universes.Count, universes[k]));
            }
        }

        /// <summary>Geçici hatalarda (lisans dolu, ağ vb.) birkaç kez bekleyip yeniden dener.</summary>
        public static T WithRetry<T>(string what, RunLog log, Func<T> action)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return action();
                }
                catch (Exception ex) when (attempt < LogonAttempts)
                {
                    log.Warn(what + " başarısız (deneme " + attempt + "/" + LogonAttempts + "), " +
                             (int)LogonRetryWait.TotalMinutes + " dk sonra tekrar denenecek: " + RunLog.Describe(ex));
                    Thread.Sleep(LogonRetryWait);
                }
            }
        }

        private static DesignerHost StartDesigner(Credential credential, string cms, RunLog log)
        {
            DesignerHost d = DesignerHost.Start();
            try
            {
                d.Logon(credential, cms, AuthType);
                return d;
            }
            catch
            {
                SafeDispose(d, log);   // logon olamayan Designer'ı açık bırakma
                throw;
            }
        }

        /// <summary>Designer kapatılırken oluşabilecek her hatayı yutar; kapatma hatası asla akışı durdurmaz.</summary>
        private static void SafeDispose(DesignerHost designer, RunLog log)
        {
            if (designer == null) return;
            try { designer.Dispose(); }
            catch (Exception ex) { log.Warn("Designer kapatılırken hata (yoksayıldı): " + RunLog.Describe(ex)); }
        }
    }
}
