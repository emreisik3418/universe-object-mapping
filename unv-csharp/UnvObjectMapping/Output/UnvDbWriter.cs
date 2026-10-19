using System;
using System.Collections.Generic;
using System.Linq;
using Oracle.ManagedDataAccess.Client;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Output
{
    /// <summary>Bir universe için yazılan satır sayıları.</summary>
    public struct WriteCount
    {
        public WriteCount(int objectRows, int tableRows) { ObjectRows = objectRows; TableRows = tableRows; }
        public int ObjectRows { get; }
        public int TableRows { get; }
    }

    /// <summary>
    /// UNV verisini universe universe yazar (run ve retry modları).
    ///
    /// Neden universe başına: Bir ortamın tüm nesnelerini (GMP03'te ~750 bin) sona kadar bellekte
    /// tutmak 32-bit süreçte OutOfMemoryException'a yol açtı. Artık her universe okununca kendi
    /// satırları tek transaction'da yenilenir ve bellekten atılır:
    ///   - Okunamayan universe'in eski satırları hiç silinmez.
    ///   - Uygulama yarıda kesilse bile yazılanlar tutarlıdır (yinelenen satır oluşmaz).
    ///   - CMS'ten silinmiş universe'lerin satırları run sonunda RemoveDeleted ile temizlenir.
    /// Sadece UNV satırlarına dokunulur; Java'nın yazdığı '.unx' satırları korunur.
    /// </summary>
    public sealed class UnvDbWriter
    {
        private const string OmTable = "POPR.SDK_METADATA_UNIVERSEOM_JOB";
        private const string TmTable = "POPR.SDK_METADATA_UNIVERSETM_JOB";
        private const string OmCuid = "UNIVERSE_CUID";
        private const string TmCuid = "CUID";
        private const string OmNotUnx = "(UNIVERSE_NAME IS NULL OR LOWER(UNIVERSE_NAME) NOT LIKE '%.unx')";
        private const string TmNotUnx = "(NAME IS NULL OR LOWER(NAME) NOT LIKE '%.unx')";
        private const int ChunkSize = 900;   // Oracle IN listesi sınırı 1000

        private readonly string connectionString;

        public UnvDbWriter(string jdbcUrl, Credential credential)
        {
            connectionString = new OracleConnectionStringBuilder
            {
                DataSource = OracleMetadataWriter.ToDataSource(jdbcUrl),
                UserID = credential.Username,
                Password = credential.Password,
                ConnectionTimeout = 30,
                Pooling = false
            }.ConnectionString;
        }

        /// <summary>DB'ye bağlanılabiliyor mu (saatlerce okuma yapmadan önce kontrol).</summary>
        public void Ping()
        {
            using (var conn = new OracleConnection(connectionString))
            using (var cmd = new OracleCommand("SELECT 1 FROM DUAL", conn))
            {
                conn.Open();
                cmd.ExecuteScalar();
            }
        }

        /// <summary>
        /// Tek universe'in UNV satırlarını tek transaction'da yeniler.
        /// tables == null ise (tablolar okunamadı) table mapping satırlarına dokunulmaz.
        /// </summary>
        public WriteCount ReplaceUniverse(string system, DateTime exportDate, UniverseInfo u,
                                          List<UniverseObject> objects, List<UniverseTable> tables)
        {
            if (string.IsNullOrEmpty(u.Cuid))
                throw new InvalidOperationException("Universe CUID'i boş, yazılamaz: " + u);

            var one = new[] { u.Cuid };
            using (var conn = new OracleConnection(connectionString))
            {
                conn.Open();
                using (OracleTransaction tx = conn.BeginTransaction())
                {
                    try
                    {
                        DeleteCuids(conn, OmTable, OmCuid, OmNotUnx, system, one);
                        int om = OracleMetadataWriter.Insert(conn, system, exportDate,
                            new Dictionary<UniverseInfo, List<UniverseObject>> { { u, objects } });

                        int tm = 0;
                        if (tables != null)
                        {
                            DeleteCuids(conn, TmTable, TmCuid, TmNotUnx, system, one);
                            tm = OracleTableMappingWriter.Insert(conn, system, exportDate,
                                new Dictionary<UniverseInfo, List<UniverseTable>> { { u, tables } });
                        }

                        tx.Commit();
                        return new WriteCount(om, tm);
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// CMS'te artık olmayan (silinmiş) universe'lerin ve CUID'i boş eski satırların UNV satırlarını siler.
        /// Sadece run sonunda, CMS listesi başarıyla alınmışsa çağrılır.
        /// </summary>
        public WriteCount RemoveDeleted(string system, ICollection<string> cmsCuids)
        {
            if (cmsCuids.Count == 0)
                throw new InvalidOperationException("CMS listesi boş; güvenlik için silme yapılmaz.");
            var keep = new HashSet<string>(cmsCuids.Where(c => !string.IsNullOrEmpty(c)), StringComparer.Ordinal);

            using (var conn = new OracleConnection(connectionString))
            {
                conn.Open();
                using (OracleTransaction tx = conn.BeginTransaction())
                {
                    try
                    {
                        int om = RemoveNotIn(conn, OmTable, OmCuid, OmNotUnx, system, keep);
                        int tm = RemoveNotIn(conn, TmTable, TmCuid, TmNotUnx, system, keep);
                        tx.Commit();
                        return new WriteCount(om, tm);
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>Verilen andan sonra object mapping'e yazılmış UNV CUID'leri (retry'ın eksikleri bulması için).</summary>
        public HashSet<string> CuidsWrittenSince(string system, DateTime since)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            string sql = "SELECT DISTINCT " + OmCuid + " FROM " + OmTable +
                         " WHERE SYSTEM = :sys AND " + OmNotUnx + " AND EXPORT_DATE >= :since AND " + OmCuid + " IS NOT NULL";
            using (var conn = new OracleConnection(connectionString))
            using (var cmd = new OracleCommand(sql, conn))
            {
                cmd.BindByName = true;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                cmd.Parameters.Add("since", OracleDbType.Date).Value = since;
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) result.Add(r.GetString(0));
                }
            }
            return result;
        }

        private static int RemoveNotIn(OracleConnection conn, string table, string cuidCol, string notUnx,
                                       string system, HashSet<string> keep)
        {
            var existing = new List<string>();
            using (var cmd = new OracleCommand("SELECT DISTINCT " + cuidCol + " FROM " + table +
                                               " WHERE SYSTEM = :sys AND " + notUnx + " AND " + cuidCol + " IS NOT NULL", conn))
            {
                cmd.BindByName = true;
                cmd.CommandTimeout = 600;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) existing.Add(r.GetString(0));
                }
            }

            int removed = DeleteCuids(conn, table, cuidCol, notUnx, system, existing.Where(c => !keep.Contains(c)).ToList());

            using (var cmd = new OracleCommand("DELETE FROM " + table + " WHERE SYSTEM = :sys AND " + notUnx +
                                               " AND " + cuidCol + " IS NULL", conn))
            {
                cmd.BindByName = true;
                cmd.CommandTimeout = 600;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                removed += cmd.ExecuteNonQuery();
            }
            return removed;
        }

        private static int DeleteCuids(OracleConnection conn, string table, string cuidCol, string notUnx,
                                       string system, IList<string> cuids)
        {
            int removed = 0;
            for (int start = 0; start < cuids.Count; start += ChunkSize)
            {
                var chunk = cuids.Skip(start).Take(ChunkSize).ToList();
                string sql = "DELETE FROM " + table + " WHERE SYSTEM = :sys AND " + notUnx +
                             " AND " + cuidCol + " IN (" + string.Join(",", chunk.Select((c, i) => ":c" + i)) + ")";
                using (var cmd = new OracleCommand(sql, conn))
                {
                    cmd.BindByName = true;
                    cmd.CommandTimeout = 600;
                    cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                    for (int i = 0; i < chunk.Count; i++)
                        cmd.Parameters.Add("c" + i, OracleDbType.Varchar2).Value = chunk[i];
                    removed += cmd.ExecuteNonQuery();
                }
            }
            return removed;
        }
    }
}
