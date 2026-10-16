package com.akbank.analytics.applications.bo.output;

import com.akbank.analytics.applications.bo.model.Credential;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.model.UniverseObject;

import java.io.StringReader;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.PreparedStatement;
import java.sql.ResultSet;
import java.sql.SQLException;
import java.sql.Timestamp;
import java.sql.Types;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;

/**
 * UNX nesnelerini POPR.SDK_METADATA_UNIVERSEOM_JOB tablosuna yazar.
 * Sadece UNIVERSE_NAME LIKE '%.unx' satırlarına dokunur (C# uygulamasının UNV satırları korunur).
 */
public final class OracleMetadataWriter {

    private static final String TABLE = "POPR.SDK_METADATA_UNIVERSEOM_JOB";

    private static final String INSERT_SQL =
            "INSERT INTO " + TABLE + " (ROW_ID, EXPORT_DATE, SYSTEM, UNIVERSE_CUID, UNIVERSE_NAME, UNIVERSE_PATH, "
          + "OBJECT_ID, OBJECT_NAME, OBJECT_PATH, OBJECT_SELECT, OBJECT_WHERE, OBJECT_STATE) "
          + "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)";

    private static final int BATCH_SIZE = 500;
    private static final int MAX_FAILED_CUIDS = 900;   // Oracle IN listesi sınırı 1000

    private final String jdbcUrl;
    private final Credential credential;

    public OracleMetadataWriter(String jdbcUrl, Credential credential) {
        this.jdbcUrl = jdbcUrl;
        this.credential = credential;
        DriverManager.setLoginTimeout(30);
    }

    /**
     * Bir sistemin UNX verisini tek transaction'da yeniler.
     * failedCuids içindeki universe'lerin eski satırları silinmez.
     * @return eklenen satır sayısı
     */
    public int replaceSystem(String system, Timestamp exportDate,
                             Map<UniverseInfo, List<UniverseObject>> data,
                             Set<String> failedCuids) throws SQLException {
        if (failedCuids.size() > MAX_FAILED_CUIDS) {
            throw new SQLException("Çok fazla universe okunamadı (" + failedCuids.size()
                    + "), " + system + " için DB'ye yazılmıyor.");
        }
        try (Connection c = connect()) {
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

    public int countBySystem(String system) throws SQLException {
        try (Connection c = connect();
             PreparedStatement ps = c.prepareStatement("SELECT COUNT(*) FROM " + TABLE + " WHERE SYSTEM = ?")) {
            ps.setString(1, system);
            try (ResultSet rs = ps.executeQuery()) {
                rs.next();
                return rs.getInt(1);
            }
        }
    }

    private Connection connect() throws SQLException {
        return DriverManager.getConnection(jdbcUrl, credential.getUsername(), credential.getPassword());
    }

    private static int delete(Connection c, String system, Set<String> failedCuids) throws SQLException {
        StringBuilder sql = new StringBuilder(
                "DELETE FROM " + TABLE + " WHERE SYSTEM = ? AND LOWER(UNIVERSE_NAME) LIKE '%.unx'");
        if (!failedCuids.isEmpty()) {
            sql.append(" AND (UNIVERSE_CUID IS NULL OR UNIVERSE_CUID NOT IN (");
            sql.append(String.join(",", Collections.nCopies(failedCuids.size(), "?")));
            sql.append("))");
        }
        try (PreparedStatement ps = c.prepareStatement(sql.toString())) {
            ps.setQueryTimeout(600);
            int i = 1;
            ps.setString(i++, system);
            for (String cuid : failedCuids) {
                ps.setString(i++, cuid);
            }
            return ps.executeUpdate();
        }
    }

    private static int insert(Connection c, String system, Timestamp exportDate,
                              Map<UniverseInfo, List<UniverseObject>> data) throws SQLException {
        int total = 0;
        int pending = 0;
        try (PreparedStatement ps = c.prepareStatement(INSERT_SQL)) {
            for (Map.Entry<UniverseInfo, List<UniverseObject>> e : data.entrySet()) {
                UniverseInfo u = e.getKey();
                List<UniverseObject> objects = e.getValue();

                if (objects.isEmpty()) {
                    // Eski davranış: nesnesi olmayan universe için tek satır
                    bindUniverse(ps, system, exportDate, u);
                    for (int col = 7; col <= 12; col++) ps.setNull(col, Types.VARCHAR);
                    ps.addBatch();
                    pending++;
                    total++;
                } else {
                    for (UniverseObject o : objects) {
                        bindUniverse(ps, system, exportDate, u);
                        ps.setString(7, o.getId());
                        ps.setString(8, o.getName());
                        ps.setString(9, o.getPath());
                        setClob(ps, 10, o.getSelect());
                        setClob(ps, 11, o.getWhere());
                        ps.setString(12, o.getState());
                        ps.addBatch();
                        pending++;
                        total++;
                        if (pending >= BATCH_SIZE) {
                            ps.executeBatch();
                            pending = 0;
                        }
                    }
                }
            }
            if (pending > 0) {
                ps.executeBatch();
            }
        }
        return total;
    }

    private static void bindUniverse(PreparedStatement ps, String system, Timestamp exportDate, UniverseInfo u)
            throws SQLException {
        ps.setString(1, UUID.randomUUID().toString().replace("-", ""));   // Hibernate "uuid" ile aynı format
        ps.setTimestamp(2, exportDate);
        ps.setString(3, system);
        ps.setString(4, u.getCuid());
        ps.setString(5, u.getName());
        ps.setString(6, u.getFolderPath());
    }

    private static void setClob(PreparedStatement ps, int index, String value) throws SQLException {
        if (value == null) {
            ps.setNull(index, Types.CLOB);
        } else {
            ps.setCharacterStream(index, new StringReader(value), value.length());
        }
    }
}
