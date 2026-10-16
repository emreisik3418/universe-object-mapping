package com.akbank.analytics.applications.bo.app;

import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.CredentialException;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.BoEnvironment;
import com.akbank.analytics.applications.bo.model.SboxTarget;

import java.nio.file.Paths;

/** Test: her ortam + DB için Sbox'tan kimlik bilgisi alınabiliyor mu. */
public class SboxCheck {

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.out.println("Kullanım: SboxCheck <credentials.json yolu>");
            return;
        }
        AppConfig config = AppConfig.load(Paths.get(args[0]));
        System.out.println("Ortam sayısı: " + config.getEnvironments().size());

        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        for (BoEnvironment env : config.getEnvironments()) {
            check("BO  " + env.getCms(), sbox, env.getSbox());
        }
        check("DB", sbox, config.getDbTarget());
    }

    private static void check(String label, SboxCredentialProvider sbox, SboxTarget target) {
        try {
            System.out.println("[OK]   " + label + " -> " + sbox.get(target));
        } catch (CredentialException e) {
            System.out.println("[HATA] " + label + " -> " + e.getMessage());
            if (e.getCause() != null) {
                System.out.println("       sebep: " + e.getCause());
            }
        }
    }
}
