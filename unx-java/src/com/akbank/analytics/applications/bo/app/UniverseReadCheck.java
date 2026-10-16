package com.akbank.analytics.applications.bo.app;

import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.BoEnvironment;
import com.akbank.analytics.applications.bo.model.UniverseContent;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.model.UniverseObject;
import com.akbank.analytics.applications.bo.sdk.BoSession;
import com.akbank.analytics.applications.bo.sdk.UniverseCatalog;
import com.akbank.analytics.applications.bo.sdk.UniverseReader;

import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

/** Test: ilk ortamda tek bir UNX'in nesnelerini ve tablolarını okur. */
public class UniverseReadCheck {

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.out.println("Kullanım: UniverseReadCheck <credentials.json> [universe adı içinde geçen metin]");
            return;
        }
        String filter = args.length > 1 ? args[1].toLowerCase() : null;

        AppConfig config = AppConfig.load(Paths.get(args[0]));
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        BoEnvironment env = config.getEnvironments().get(0);
        Path workDir = Paths.get(System.getProperty("user.dir"), "retrieve");
        System.out.println("=== " + env.getCms() + " ===");

        try (BoSession session = BoSession.logon(env.getCms(), sbox.get(env.getSbox()), "secEnterprise");
             UniverseReader reader = new UniverseReader(session, workDir)) {

            UniverseInfo target = null;
            for (UniverseInfo u : UniverseCatalog.listUnx(session.getInfoStore())) {
                if (filter == null || u.getName().toLowerCase().contains(filter)) {
                    target = u;
                    break;
                }
            }
            if (target == null) {
                System.out.println("[HATA] Eşleşen universe bulunamadı.");
                return;
            }

            System.out.println("Okunuyor: " + target);
            long start = System.currentTimeMillis();
            UniverseContent content = reader.read(target);
            List<UniverseObject> objects = content.getObjects();
            System.out.println("[OK]   Nesne sayısı: " + objects.size()
                    + "  Süre: " + (System.currentTimeMillis() - start) + " ms");

            Map<String, Integer> byType = new TreeMap<>();
            for (UniverseObject o : objects) byType.merge(o.getType(), 1, Integer::sum);
            System.out.println("       Tip dağılımı: " + byType);
            for (int i = 0; i < Math.min(15, objects.size()); i++) {
                System.out.println("       " + objects.get(i));
            }

            System.out.println("[OK]   Tablo sayısı: " + content.getTables().size()
                    + (content.getTableError() != null ? "  (TABLO HATASI: " + content.getTableError() + ")" : ""));
            for (int k = 0; k < Math.min(15, content.getTables().size()); k++) {
                System.out.println("       T: " + content.getTables().get(k));
            }
        }
        System.out.println("[OK]   Kapatıldı ve logoff yapıldı.");
    }
}
