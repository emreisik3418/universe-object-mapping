using System;
using System.Collections.Generic;
using System.Threading;
using UnvObjectMapping.Model;
using UnvObjectMapping.Sdk;

namespace UnvObjectMapping.App
{
    /// <summary>Bir ortamdaki universe listesini Designer ile okumanın sonucu.</summary>
    internal sealed class BatchResult
    {
        public readonly Dictionary<UniverseInfo, List<UniverseObject>> Objects = new Dictionary<UniverseInfo, List<UniverseObject>>();
        public readonly Dictionary<UniverseInfo, List<UniverseTable>> Tables = new Dictionary<UniverseInfo, List<UniverseTable>>();
        public readonly List<string> FailedCuids = new List<string>();
        public readonly List<string> FailedTableCuids = new List<string>();
        public int ObjectCount;
        public int TableCount;
    }

    /// <summary>
    /// "run" ve "retry" modlarının ortak okuma döngüsü.
    ///
    /// - Hata veren universe loglanır ve atlanır.
    /// - Designer her RestartEvery universe'te bir ve HER HATADAN SONRA yeniden başlatılır.
    /// - Bir universe UniverseTimeout süresini aşarsa Designer süreci sonlandırılır (donma koruması).
    /// - Designer hiç açılamazsa kalan universe'ler tek tek "ATLANDI" olarak loglanır.
    ///
    /// Log satır formatı ("HATA  ", "ATLANDI", "Tablolar okunamadı" + " [CUID]") retry modunun
    /// log'dan hatalıları bulması için kullanılır; değiştirirken RetryRun.ParseFailed'ı da güncelleyin.
    /// </summary>
    internal static class UniverseBatchReader
    {
        public const string AuthType = "secEnterprise";
        private const int RestartEvery = 50;

        // Logon (CMS veya Designer) başarısız olursa: ör. "580 Concurrent Access Licenses are in use"
        private const int LogonAttempts = 4;
        private static readonly TimeSpan LogonRetryWait = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan UniverseTimeout = TimeSpan.FromMinutes(10);

        public static BatchResult Read(IList<UniverseInfo> universes, Credential credential, string cms, RunLog log)
        {
            var result = new BatchResult();
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
                        SafeDispose(designer, log);
                        designer = null;
                        try
                        {
                            designer = WithRetry(cms + " Designer başlatma/logon", log, () => StartDesigner(credential, cms, log));
                        }
                        catch (Exception ex)
                        {
                            // Designer hiç açılamıyor: kalan universe'leri "hatalı" say (eski satırları korunur),
                            // okunanları KAYBETME. Her biri ayrı satırda loglanır ki retry bulabilsin.
                            log.Error(label + " Designer açılamadı, kalan " + (universes.Count - i) +
                                      " UNV atlandı (eski satırları korunacak)", ex);
                            for (int k = i; k < universes.Count; k++)
                            {
                                result.FailedCuids.Add(universes[k].Cuid);
                                result.FailedTableCuids.Add(universes[k].Cuid);
                                log.Error(string.Format("[{0}] {1}/{2} ATLANDI {3}", cms, k + 1, universes.Count, universes[k]));
                            }
                            break;
                        }
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
                            UniverseContent content = designer.Read(u);
                            result.Objects[u] = content.Objects;
                            result.ObjectCount += content.Objects.Count;

                            if (content.TableError == null)
                            {
                                result.Tables[u] = content.Tables;
                                result.TableCount += content.Tables.Count;
                            }
                            else
                            {
                                result.FailedTableCuids.Add(u.Cuid);
                                log.Warn(label + " Tablolar okunamadı (nesneler OK) " + u + " : " + RunLog.Describe(content.TableError));
                            }
                            log.Info(label + " OK    " + u + " (" + content.Objects.Count + " nesne, " + content.Tables.Count + " tablo)");
                        }
                        catch (Exception ex)
                        {
                            result.FailedCuids.Add(u.Cuid);
                            result.FailedTableCuids.Add(u.Cuid);
                            log.Error(label + " HATA  " + u + (timedOut ? " (zaman aşımı, Designer sonlandırıldı)" : ""), ex);

                            // Hatadan sonra Designer'ın durumuna güvenme: kapat, sıradaki universe'te yeniden başlat
                            SafeDispose(designer, log);
                            designer = null;
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
