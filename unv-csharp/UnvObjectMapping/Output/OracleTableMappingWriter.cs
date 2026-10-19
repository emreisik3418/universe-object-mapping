using System;
using System.Collections.Generic;
using System.Linq;
using Oracle.ManagedDataAccess.Client;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Output
{
    /// <summary>
    /// UNV universe tablolarını POPR.SDK_METADATA_UNIVERSETM_JOB tablosuna yazar.
    /// Sadece UNV satırlarına dokunur; Java'nın yazdığı NAME LIKE '%.unx' satırları silinmez.
    /// </summary>
    public sealed class OracleTableMappingWriter
    {
        private const string Table = "POPR.SDK_METADATA_UNIVERSETM_JOB";
        private const string NotUnx = "(NAME IS NULL OR LOWER(NAME) NOT LIKE '%.unx')";
        private const int MaxFailedCuids = 900;   // Oracle IN listesi sınırı 1000

        private const string InsertSql =
            "INSERT INTO " + Table + " (ROW_ID, EXPORT_DATE, SYSTEM, CUID, NAME, PATH, TABLE_NAME, DERIVED_SCRIPT) " +
            "VALUES (:rid, :edate, :sys, :cuid, :uname, :upath, :tname, :dscript)";

        private readonly string connectionString;

        public OracleTableMappingWriter(string jdbcUrl, Credential credential)
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

        /// <summary>Bir sistemin UNV tablo eşlemesini tek transaction'da yeniler. failedCuids'in eski satırları korunur.</summary>
        public int ReplaceSystem(string system, DateTime exportDate,
                                 IDictionary<UniverseInfo, List<UniverseTable>> data,
                                 ICollection<string> failedCuids)
        {
            if (failedCuids.Count > MaxFailedCuids)
                throw new InvalidOperationException("Çok fazla universe okunamadı (" + failedCuids.Count +
                                                    "), " + system + " için tablo eşlemesi yazılmıyor.");

            using (var conn = new OracleConnection(connectionString))
            {
                conn.Open();
                using (OracleTransaction tx = conn.BeginTransaction())
                {
                    try
                    {
                        Delete(conn, system, failedCuids);
                        int inserted = Insert(conn, system, exportDate, data);
                        tx.Commit();
                        return inserted;
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
        /// Sadece verilen universe'lerin (CUID) UNV satırlarını tek transaction'da yeniler
        /// (retry modu). Sistemin diğer universe'lerine dokunulmaz.
        /// </summary>
        /// <returns>Eklenen satır sayısı</returns>
        public int ReplaceUniverses(string system, DateTime exportDate,
                                    IDictionary<UniverseInfo, List<UniverseTable>> data)
        {
            if (data.Count == 0) return 0;
            using (var conn = new OracleConnection(connectionString))
            {
                conn.Open();
                using (OracleTransaction tx = conn.BeginTransaction())
                {
                    try
                    {
                        var cuids = data.Keys.Select(u => u.Cuid).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
                        for (int start = 0; start < cuids.Count; start += MaxFailedCuids)
                        {
                            var chunk = cuids.Skip(start).Take(MaxFailedCuids).ToList();
                            string sql = "DELETE FROM " + Table + " WHERE SYSTEM = :sys AND " + NotUnx +
                                         " AND CUID IN (" + string.Join(",", chunk.Select((c, i) => ":c" + i)) + ")";
                            using (var cmd = new OracleCommand(sql, conn))
                            {
                                cmd.BindByName = true;
                                cmd.CommandTimeout = 600;
                                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                                for (int i = 0; i < chunk.Count; i++)
                                    cmd.Parameters.Add("c" + i, OracleDbType.Varchar2).Value = chunk[i];
                                cmd.ExecuteNonQuery();
                            }
                        }
                        int inserted = Insert(conn, system, exportDate, data);
                        tx.Commit();
                        return inserted;
                    }
                    catch
                    {
                        tx.Rollback();
                        throw;
                    }
                }
            }
        }

        public int CountUnvRows(string system)
        {
            using (var conn = new OracleConnection(connectionString))
            using (var cmd = new OracleCommand("SELECT COUNT(*) FROM " + Table + " WHERE SYSTEM = :sys AND " + NotUnx, conn))
            {
                cmd.BindByName = true;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                conn.Open();
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>SYSTEM / tür (UNX-UNV) / export zamanı bazında özet (salt okuma).</summary>
        public List<string[]> Report()
        {
            const string tur = "CASE WHEN LOWER(NAME) LIKE '%.unx' THEN 'UNX' ELSE 'UNV' END";
            const string sql =
                "SELECT SYSTEM, " + tur + ", TO_CHAR(EXPORT_DATE, 'YYYY-MM-DD HH24:MI'), COUNT(*), COUNT(DISTINCT CUID) " +
                "FROM " + Table + " GROUP BY SYSTEM, " + tur + ", TO_CHAR(EXPORT_DATE, 'YYYY-MM-DD HH24:MI') ORDER BY 1, 2, 3";

            var rows = new List<string[]>();
            using (var conn = new OracleConnection(connectionString))
            using (var cmd = new OracleCommand(sql, conn))
            {
                conn.Open();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => Convert.ToString(r.GetValue(i))).ToArray());
                }
            }
            return rows;
        }

        private static void Delete(OracleConnection conn, string system, ICollection<string> failedCuids)
        {
            string sql = "DELETE FROM " + Table + " WHERE SYSTEM = :sys AND " + NotUnx;
            var cuids = failedCuids.ToList();
            if (cuids.Count > 0)
            {
                sql += " AND (CUID IS NULL OR CUID NOT IN (" +
                       string.Join(",", cuids.Select((c, i) => ":c" + i)) + "))";
            }

            using (var cmd = new OracleCommand(sql, conn))
            {
                cmd.BindByName = true;
                cmd.CommandTimeout = 600;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                for (int i = 0; i < cuids.Count; i++)
                    cmd.Parameters.Add("c" + i, OracleDbType.Varchar2).Value = cuids[i];
                cmd.ExecuteNonQuery();
            }
        }

        private static int Insert(OracleConnection conn, string system, DateTime exportDate,
                                  IDictionary<UniverseInfo, List<UniverseTable>> data)
        {
            int total = 0;
            using (var cmd = new OracleCommand(InsertSql, conn))
            {
                cmd.BindByName = true;
                var rid = cmd.Parameters.Add("rid", OracleDbType.Varchar2);
                cmd.Parameters.Add("edate", OracleDbType.Date).Value = exportDate;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                var cuid = cmd.Parameters.Add("cuid", OracleDbType.Varchar2);
                var uname = cmd.Parameters.Add("uname", OracleDbType.Varchar2);
                var upath = cmd.Parameters.Add("upath", OracleDbType.Varchar2);
                var tname = cmd.Parameters.Add("tname", OracleDbType.Varchar2);
                var dscript = cmd.Parameters.Add("dscript", OracleDbType.Clob);

                foreach (var entry in data)
                {
                    UniverseInfo u = entry.Key;
                    cuid.Value = Db(u.Cuid);
                    uname.Value = Db(u.Name);
                    upath.Value = Db(u.FolderPath);

                    foreach (UniverseTable t in entry.Value)
                    {
                        rid.Value = Guid.NewGuid().ToString("N");
                        tname.Value = Db(t.Name);
                        dscript.Value = Db(t.DerivedScript);
                        cmd.ExecuteNonQuery();
                        total++;
                    }
                }
            }
            return total;
        }

        private static object Db(string value) => string.IsNullOrEmpty(value) ? (object)DBNull.Value : value;
    }
}
