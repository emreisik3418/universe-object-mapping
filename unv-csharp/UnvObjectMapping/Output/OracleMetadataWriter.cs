using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Oracle.ManagedDataAccess.Client;
using UnvObjectMapping.Model;

namespace UnvObjectMapping.Output
{
    /// <summary>
    /// UNV nesnelerini POPR.SDK_METADATA_UNIVERSEOM_JOB tablosuna yazar (ODP.NET Managed).
    ///
    /// Kural: Bu uygulama SADECE UNV satırlarına dokunur. Java uygulamasının yazdığı
    /// UNIVERSE_NAME LIKE '%.unx' satırları silinmez.
    /// </summary>
    public sealed class OracleMetadataWriter
    {
        private const string Table = "POPR.SDK_METADATA_UNIVERSEOM_JOB";
        private const string NotUnx = "(UNIVERSE_NAME IS NULL OR LOWER(UNIVERSE_NAME) NOT LIKE '%.unx')";
        private const int MaxFailedCuids = 900;   // Oracle IN listesi sınırı 1000

        private const string InsertSql =
            "INSERT INTO " + Table + " (ROW_ID, EXPORT_DATE, SYSTEM, UNIVERSE_CUID, UNIVERSE_NAME, UNIVERSE_PATH, " +
            "OBJECT_ID, OBJECT_NAME, OBJECT_PATH, OBJECT_SELECT, OBJECT_WHERE, OBJECT_STATE) " +
            "VALUES (:rid, :edate, :sys, :ucuid, :uname, :upath, :oid, :oname, :opath, :osel, :owhere, :ostate)";

        private readonly string connectionString;

        public OracleMetadataWriter(string jdbcUrl, Credential credential)
        {
            connectionString = new OracleConnectionStringBuilder
            {
                DataSource = ToDataSource(jdbcUrl),
                UserID = credential.Username,
                Password = credential.Password,
                ConnectionTimeout = 30,
                Pooling = false
            }.ConnectionString;
        }

        /// <summary>"jdbc:oracle:thin:@//host:port/servis" -> "//host:port/servis" (EZConnect). TNS tanımı ise aynen.</summary>
        public static string ToDataSource(string jdbcUrl)
        {
            const string prefix = "jdbc:oracle:thin:@";
            return jdbcUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? jdbcUrl.Substring(prefix.Length)
                : jdbcUrl;
        }

        /// <summary>
        /// Bir sistemin UNV verisini tek transaction'da yeniler.
        /// failedCuids içindeki universe'lerin eski satırları silinmez.
        /// </summary>
        /// <returns>Eklenen satır sayısı</returns>
        public int ReplaceSystem(string system, DateTime exportDate,
                                 IDictionary<UniverseInfo, List<UniverseObject>> data,
                                 ICollection<string> failedCuids)
        {
            if (failedCuids.Count > MaxFailedCuids)
                throw new InvalidOperationException("Çok fazla universe okunamadı (" + failedCuids.Count +
                                                    "), " + system + " için DB'ye yazılmıyor.");

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
            const string sql =
                "SELECT SYSTEM, " +
                "       CASE WHEN LOWER(UNIVERSE_NAME) LIKE '%.unx' THEN 'UNX' ELSE 'UNV' END AS TUR, " +
                "       TO_CHAR(EXPORT_DATE, 'YYYY-MM-DD HH24:MI') AS ZAMAN, " +
                "       COUNT(*) AS SATIR, COUNT(DISTINCT UNIVERSE_CUID) AS UNIVERSE " +
                "FROM " + Table + " " +
                "GROUP BY SYSTEM, CASE WHEN LOWER(UNIVERSE_NAME) LIKE '%.unx' THEN 'UNX' ELSE 'UNV' END, " +
                "         TO_CHAR(EXPORT_DATE, 'YYYY-MM-DD HH24:MI') " +
                "ORDER BY 1, 2, 3";

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
                sql += " AND (UNIVERSE_CUID IS NULL OR UNIVERSE_CUID NOT IN (" +
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

        internal static int Insert(OracleConnection conn, string system, DateTime exportDate,
                                  IDictionary<UniverseInfo, List<UniverseObject>> data)
        {
            int total = 0;
            using (var cmd = new OracleCommand(InsertSql, conn))
            {
                cmd.BindByName = true;
                var rid = cmd.Parameters.Add("rid", OracleDbType.Varchar2);
                cmd.Parameters.Add("edate", OracleDbType.Date).Value = exportDate;
                cmd.Parameters.Add("sys", OracleDbType.Varchar2).Value = system;
                var ucuid = cmd.Parameters.Add("ucuid", OracleDbType.Varchar2);
                var uname = cmd.Parameters.Add("uname", OracleDbType.Varchar2);
                var upath = cmd.Parameters.Add("upath", OracleDbType.Varchar2);
                var oid = cmd.Parameters.Add("oid", OracleDbType.Varchar2);
                var oname = cmd.Parameters.Add("oname", OracleDbType.Varchar2);
                var opath = cmd.Parameters.Add("opath", OracleDbType.Varchar2);
                var osel = cmd.Parameters.Add("osel", OracleDbType.Clob);
                var owhere = cmd.Parameters.Add("owhere", OracleDbType.Clob);
                var ostate = cmd.Parameters.Add("ostate", OracleDbType.Varchar2);

                foreach (var entry in data)
                {
                    UniverseInfo u = entry.Key;
                    ucuid.Value = Db(u.Cuid);
                    uname.Value = Db(u.Name);
                    upath.Value = Db(u.FolderPath);

                    // Eski davranış: nesnesi olmayan universe için nesne kolonları boş tek satır
                    IEnumerable<UniverseObject> objects = entry.Value.Count > 0
                        ? (IEnumerable<UniverseObject>)entry.Value
                        : new UniverseObject[] { null };

                    foreach (UniverseObject o in objects)
                    {
                        rid.Value = Guid.NewGuid().ToString("N");   // Hibernate "uuid" ile aynı format (32 hex)
                        oid.Value = Db(o?.Id);
                        oname.Value = Db(o?.Name);
                        opath.Value = Db(o?.Path);
                        osel.Value = Db(o?.Select);
                        owhere.Value = Db(o?.Where);
                        ostate.Value = Db(o?.State);
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
