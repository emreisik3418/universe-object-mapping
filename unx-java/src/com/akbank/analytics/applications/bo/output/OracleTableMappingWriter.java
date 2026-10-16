package com.akbank.analytics.applications.bo.output;

import com.akbank.analytics.applications.bo.model.Credential;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.model.UniverseTable;

import java.io.StringReader;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.sql.Types;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

/** UNX universe tablolarını POPR.SDK_METADATA_UNIVERSETM_JOB tablosuna yazar. Sadece '.unx' satırlarına dokunur. */
public final class OracleTableMappingWriter {

    private static final String TABLE = "POPR.SDK_METADATA_UNIVERSETM_JOB";

    private static final String INSERT_SQL =
            "INSERT INTO " + TABLE + " (ROW_ID, EXPORT_DATE, SYSTEM, CUID, NAME, PATH, TABLE_NAME, DERIVED_SCRIPT) "
          + "VALUES (?,?,?,?,?,?,?,?)";

    private static final int BATCH_SIZE = 500;
    private static final int MAX_FAILED_CUIDS = 900;   // Oracle IN listesi sınırı 1000

    private final String jdbcUrl;
    private final Credential credential;

    public OracleTableMappingWriter(String jdbcUrl, Credential credential) {
        this.jdbcUrl = jdbcUrl;
        this.credential = credential;
        DriverManager.setLoginTimeout(30);
    }

    /** Bir sistemin UNX tablo eşlemesini tek transaction'da yeniler. failedCuids'in eski satırları korunur. */
    public int replaceSystem(String system, Timestamp exportDate,
                             Map<UniverseInfo, List<UniverseTable>> data,
                             Set<String> failedCuids) throws SQLException {
        if (failedCuids.size() > MAX_FAILED_CUIDS) {
            throw new SQLException("Çok fazla universe okunamadı (" + failedCuids.size()
                    + "), " + system + " için tablo eşlemesi yazılmıyor.");
        }
        try (Connection c = DriverManager.getConnection(jdbcUrl, credential.getUsername(), credential.getPassword())) {
            c.setAutoCommit(false);
            try {
                delete(c, system, failedCuids);
                int inserted = insert(c, system, exportDate, data);
                c.commit();
                return inserted;
            } catch (SQLException | RuntimeException e) {
                c.rollback();
                throw e;
            }
        }
    }

    private static void delete(Connection c, String system, Set<String> failedCuids) throws SQLException {
        StringBuilder sql = new StringBuilder(
                "DELETE FROM " + TABLE + " WHERE SYSTEM = ? AND LOWER(NAME) LIKE '%.unx'");
        List<String> cuids = new ArrayList<>(failedCuids);
        if (!cuids.isEmpty()) {
            sql.append(" AND (CUID IS NULL OR CUID NOT IN (");
            sql.append(String.join(",", Collections.nCopies(cuids.size(), "?")));
            sql.append("))");
        }
        try (PreparedStatement ps = c.prepareStatement(sql.toString())) {
            ps.setQueryTimeout(600);
            int i = 1;
            ps.setString(i++, system);
            for (String cuid : cuids) ps.setString(i++, cuid);
            ps.executeUpdate();
        }
    }

    private static int insert(Connection c, String system, Timestamp exportDate,
                              Map<UniverseInfo, List<UniverseTable>> data) throws SQLException {
        int total = 0;
        int pending = 0;
        try (PreparedStatement ps = c.prepareStatement(INSERT_SQL)) {
            for (Map.Entry<UniverseInfo, List<UniverseTable>> e : data.entrySet()) {
                UniverseInfo u = e.getKey();
                for (UniverseTable t : e.getValue()) {
                    ps.setString(1, UUID.randomUUID().toString().replace("-", ""));
                    ps.setTimestamp(2, exportDate);
                    ps.setString(3, system);
                    ps.setString(4, u.getCuid());
                    ps.setString(5, u.getName());
                    ps.setString(6, u.getFolderPath());
                    ps.setString(7, t.getName());
                    if (t.getDerivedScript() == null) {
                        ps.setNull(8, Types.CLOB);
                    } else {
                        ps.setCharacterStream(8, new StringReader(t.getDerivedScript()), t.getDerivedScript().length());
                    }
                    ps.addBatch();
                    total++;
                    if (++pending >= BATCH_SIZE) {
                        ps.executeBatch();
                        pending = 0;
                    }
                }
            }
            if (pending > 0) ps.executeBatch();
        }
        return total;
    }
}
