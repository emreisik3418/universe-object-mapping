package com.akbank.analytics.applications.bo.app;

import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.Credential;

import java.nio.file.Paths;
import java.sql.Connection;
import java.sql.DriverManager;
import java.sql.ResultSet;
import java.sql.Statement;

/**
 * Bakım aracı (salt okuma):
 *   <json> report   -> OM ve TM tablolarının SYSTEM / tür (UNX-UNV) / zaman bazında özeti
 */
public class DbMaintenance {

    private static final String[][] TABLES = {
            {"POPR.SDK_METADATA_UNIVERSEOM_JOB", "UNIVERSE_NAME", "UNIVERSE_CUID"},
            {"POPR.SDK_METADATA_UNIVERSETM_JOB", "NAME", "CUID"}
    };

    public static void main(String[] args) throws Exception {
        if (args.length < 2 || !"report".equals(args[1])) {
            System.out.println("Kullanım: DbMaintenance <credentials.json> report");
            return;
        }
        AppConfig config = AppConfig.load(Paths.get(args[0]));
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());
        Credential db = sbox.get(config.getDbTarget());

        DriverManager.setLoginTimeout(30);
        try (Connection c = DriverManager.getConnection(config.getDbJdbcUrl(), db.getUsername(), db.getPassword())) {
            for (String[] t : TABLES) {
                report(c, t[0], t[1], t[2]);
                System.out.println();
            }
        }
    }

    private static void report(Connection c, String table, String nameCol, String cuidCol) throws Exception {
        String tur = "CASE WHEN LOWER(" + nameCol + ") LIKE '%.unx' THEN 'UNX' ELSE 'UNV' END";
        String sql = "SELECT SYSTEM, " + tur + ", TO_CHAR(EXPORT_DATE, 'YYYY-MM-DD HH24:MI'), COUNT(*), "
                   + "COUNT(DISTINCT " + cuidCol + ") FROM " + table
                   + " GROUP BY SYSTEM, " + tur + ", TO_CHAR(EXPORT_DATE, 'YYYY-MM-DD HH24:MI') ORDER BY 1, 2, 3";

        System.out.println("=== " + table + " ===");
        System.out.println(String.format("%-30s %-4s %-17s %10s %10s", "SYSTEM", "TUR", "ZAMAN", "SATIR", "UNIVERSE"));
        System.out.println(repeat('-', 75));
        try (Statement st = c.createStatement(); ResultSet rs = st.executeQuery(sql)) {
            while (rs.next()) {
                System.out.println(String.format("%-30s %-4s %-17s %10d %10d",
                        rs.getString(1), rs.getString(2), rs.getString(3), rs.getLong(4), rs.getLong(5)));
            }
        }
    }

    private static String repeat(char ch, int n) {
        StringBuilder sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.append(ch);
        return sb.toString();
    }
}
