package com.akbank.analytics.applications.bo.app;

import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.BoEnvironment;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.model.UniverseObject;
import com.akbank.analytics.applications.bo.output.OracleMetadataWriter;
import com.akbank.analytics.applications.bo.sdk.BoSession;
import com.akbank.analytics.applications.bo.sdk.UniverseCatalog;
import com.akbank.analytics.applications.bo.sdk.UniverseReader;

import java.nio.file.Paths;
import java.sql.Timestamp;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** Test: tek universe'ü SYSTEM='DBTEST' olarak yazar, sayar, siler. Gerçek veriye dokunmaz. */
public class DbWriteCheck {

    private static final String TEST_SYSTEM = "DBTEST";

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.out.println("Kullanım: DbWriteCheck <credentials.json>");
            return;
        }
        AppConfig config = AppConfig.load(Paths.get(args[0]));
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        Map<UniverseInfo, List<UniverseObject>> data = new LinkedHashMap<>();
        BoEnvironment env = config.getEnvironments().get(0);
        try (BoSession session = BoSession.logon(env.getCms(), sbox.get(env.getSbox()), "secEnterprise");
             UniverseReader reader = new UniverseReader(session,
                     Paths.get(System.getProperty("user.dir"), "retrieve"))) {
            UniverseInfo first = UniverseCatalog.listUnx(session.getInfoStore()).get(0);
            data.put(first, reader.read(first).getObjects());
            System.out.println("[OK]   Okundu: " + first + " -> " + data.get(first).size() + " nesne");
        }

        OracleMetadataWriter writer = new OracleMetadataWriter(
                config.getDbJdbcUrl(), sbox.get(config.getDbTarget()));
        Timestamp now = new Timestamp(System.currentTimeMillis());

        int inserted = writer.replaceSystem(TEST_SYSTEM, now, data, Collections.<String>emptySet());
        System.out.println("[OK]   Eklenen satır: " + inserted);
        System.out.println("[OK]   Tablodaki DBTEST satırı: " + writer.countBySystem(TEST_SYSTEM));

        writer.replaceSystem(TEST_SYSTEM, now,
                Collections.<UniverseInfo, List<UniverseObject>>emptyMap(), Collections.<String>emptySet());
        System.out.println("[OK]   Temizlendi. Kalan DBTEST satırı: " + writer.countBySystem(TEST_SYSTEM));
    }
}
