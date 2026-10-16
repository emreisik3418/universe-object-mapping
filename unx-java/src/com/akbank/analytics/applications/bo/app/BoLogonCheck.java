package com.akbank.analytics.applications.bo.app;

import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.BoEnvironment;
import com.akbank.analytics.applications.bo.model.Credential;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.sdk.BoSession;
import com.akbank.analytics.applications.bo.sdk.UniverseCatalog;

import java.nio.file.Paths;
import java.util.List;

/** Test: her ortamda logon + UNX listesi + logoff. */
public class BoLogonCheck {

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.out.println("Kullanım: BoLogonCheck <credentials.json> [authType]");
            return;
        }
        String authType = args.length > 1 ? args[1] : "secEnterprise";

        AppConfig config = AppConfig.load(Paths.get(args[0]));
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        for (BoEnvironment env : config.getEnvironments()) {
            System.out.println("=== " + env.getCms() + " ===");
            long start = System.currentTimeMillis();
            try {
                Credential credential = sbox.get(env.getSbox());

                try (BoSession session = BoSession.logon(env.getCms(), credential, authType)) {
                    System.out.println("[OK]   Logon başarılı");

                    List<UniverseInfo> universes = UniverseCatalog.listUnx(session.getInfoStore());
                    System.out.println("[OK]   UNX sayısı: " + universes.size());
                    for (int i = 0; i < Math.min(10, universes.size()); i++) {
                        System.out.println("       " + universes.get(i));
                    }
                }   // <- logoff burada, hata olsa bile
                System.out.println("[OK]   Logoff. Süre: " + (System.currentTimeMillis() - start) + " ms");

            } catch (Throwable t) {
                System.out.println("[HATA] " + t);
                t.printStackTrace(System.out);
            }
        }
    }
}
