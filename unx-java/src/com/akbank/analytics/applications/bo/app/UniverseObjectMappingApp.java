package com.akbank.analytics.applications.bo.app;

import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.BoEnvironment;
import com.akbank.analytics.applications.bo.model.Credential;
import com.akbank.analytics.applications.bo.model.UniverseContent;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.model.UniverseObject;
import com.akbank.analytics.applications.bo.model.UniverseTable;
import com.akbank.analytics.applications.bo.output.OracleMetadataWriter;
import com.akbank.analytics.applications.bo.output.OracleTableMappingWriter;
import com.akbank.analytics.applications.bo.sdk.BoSession;
import com.akbank.analytics.applications.bo.sdk.UniverseCatalog;
import com.akbank.analytics.applications.bo.sdk.UniverseReader;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.sql.Timestamp;
import java.util.Comparator;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.stream.Stream;

/**
 * Ana uygulama (UNX): tüm ortamlar, tüm universe'ler.
 * Her universe tek kez retrieve edilir; nesneler -> SDK_METADATA_UNIVERSEOM_JOB,
 * tablolar -> SDK_METADATA_UNIVERSETM_JOB (ayrı transaction'lar).
 * Çıkış kodu: 0 = başarılı, 1 = bazı universe/ortamlarda hata, 2 = uygulama başlayamadı.
 */
public class UniverseObjectMappingApp {

    private static final String AUTH_TYPE = System.getProperty("bo.authType", "secEnterprise");

    // CMS logon başarısız olursa (ör. "580 Concurrent Access Licenses are in use") bekleyip yeniden dene
    private static final int LOGON_ATTEMPTS = 4;
    private static final long LOGON_RETRY_WAIT_MS = 2 * 60 * 1000L;

    public static void main(String[] args) {
        int exitCode;
        RunLog log = null;
        try {
            Path base = Paths.get(System.getProperty("user.dir"));
            log = RunLog.open(base.resolve("logs"));
            exitCode = run(args, base, log);
        } catch (Throwable t) {
            if (log != null) {
                log.error("Beklenmeyen hata, uygulama durduruldu", t);
            } else {
                t.printStackTrace();
            }
            exitCode = 2;
        } finally {
            if (log != null) log.close();
        }
        System.exit(exitCode);   // BO SDK'nın arka plan thread'leri JVM'i açık tutmasın
    }

    private static int run(String[] args, Path base, RunLog log) throws Exception {
        if (args.length < 1) {
            log.error("Kullanım: UniverseObjectMappingApp <credentials.json>");
            return 2;
        }
        long runStart = System.currentTimeMillis();
        log.info("Başladı. Log: " + log.getPath());

        AppConfig config = AppConfig.load(Paths.get(args[0]));
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        // DB kimlik bilgisini en başta al: sorun varsa saatlerce okuma yaptıktan sonra değil, hemen öğrenelim
        Credential dbCredential = sbox.get(config.getDbTarget());
        OracleMetadataWriter writer = new OracleMetadataWriter(config.getDbJdbcUrl(), dbCredential);
        OracleTableMappingWriter tableWriter = new OracleTableMappingWriter(config.getDbJdbcUrl(), dbCredential);

        Path workDir = base.resolve("retrieve");
        cleanDirectory(workDir, log);

        Timestamp exportDate = new Timestamp(runStart);
        boolean allOk = true;

        for (BoEnvironment env : config.getEnvironments()) {
            try {
                allOk &= processEnvironment(env, sbox, writer, tableWriter, workDir, exportDate, log);
            } catch (Throwable t) {
                log.error("[" + env.getCms() + "] Ortam işlenemedi, DB'ye yazılmadı", t);
                allOk = false;
                if (t instanceof Error && !(t instanceof NoClassDefFoundError)) {
                    throw (Error) t;            // OutOfMemoryError vb. durumlarda devam etmek güvenli değil
                }
            }
        }

        long minutes = (System.currentTimeMillis() - runStart) / 60000;
        log.info("Bitti. Süre: " + minutes + " dk. Sonuç: " + (allOk ? "BAŞARILI" : "HATALI (detaylar logda)"));
        return allOk ? 0 : 1;
    }

    /** @return ortamdaki her şey sorunsuzsa true */
    private static boolean processEnvironment(BoEnvironment env, SboxCredentialProvider sbox,
                                              OracleMetadataWriter writer, OracleTableMappingWriter tableWriter,
                                              Path workDir, Timestamp exportDate, RunLog log) throws Exception {
        String cms = env.getCms();
        long start = System.currentTimeMillis();
        Map<UniverseInfo, List<UniverseObject>> objectData = new LinkedHashMap<>();
        Map<UniverseInfo, List<UniverseTable>> tableData = new LinkedHashMap<>();
        Set<String> failedCuids = new LinkedHashSet<>();
        Set<String> failedTableCuids = new LinkedHashSet<>();
        int total;
        int objectCount = 0;
        int tableCount = 0;

        try (BoSession session = logonWithRetry(cms, sbox.get(env.getSbox()), log);
             UniverseReader reader = new UniverseReader(session, workDir)) {

            List<UniverseInfo> universes = UniverseCatalog.listUnx(session.getInfoStore());
            total = universes.size();
            log.info("[" + cms + "] Logon OK. UNX sayısı: " + total);

            int i = 0;
            for (UniverseInfo u : universes) {
                i++;
                try {
                    UniverseContent content = reader.read(u);
                    objectData.put(u, content.getObjects());
                    objectCount += content.getObjects().size();

                    if (content.getTableError() == null) {
                        tableData.put(u, content.getTables());
                        tableCount += content.getTables().size();
                    } else {
                        failedTableCuids.add(u.getCuid());
                        log.warn(String.format("[%s] %d/%d Tablolar okunamadı (nesneler OK) %s : %s",
                                cms, i, total, u.getCmsPath(), RunLog.describe(content.getTableError())));
                    }
                    log.info(String.format("[%s] %d/%d OK    %s (%d nesne, %d tablo)",
                            cms, i, total, u.getCmsPath(), content.getObjects().size(), content.getTables().size()));
                } catch (Exception e) {
                    failedCuids.add(u.getCuid());
                    failedTableCuids.add(u.getCuid());
                    log.error(String.format("[%s] %d/%d HATA  %s", cms, i, total, u.getCmsPath()), e);
                }
            }
        }   // <- SlContext kapanır, logoff yapılır

        if (total == 0) {
            log.warn("[" + cms + "] Hiç UNX listelenmedi. Güvenlik için DB'ye dokunulmadı.");
            return false;
        }
        if (objectData.isEmpty()) {
            log.error("[" + cms + "] Hiçbir universe okunamadı. Güvenlik için DB'ye dokunulmadı.");
            return false;
        }

        boolean ok = failedCuids.isEmpty() && failedTableCuids.isEmpty();

        // Object mapping ve table mapping ayrı transaction: birinin hatası diğerini engellemez
        try {
            int inserted = writer.replaceSystem(cms, exportDate, objectData, failedCuids);
            log.info(String.format("[%s] Object mapping yazıldı: %d satır (%d universe, %d nesne, hatalı %d)",
                    cms, inserted, objectData.size(), objectCount, failedCuids.size()));
        } catch (Exception e) {
            ok = false;
            log.error("[" + cms + "] Object mapping DB'ye yazılamadı (eski veri korundu)", e);
        }

        if (tableData.isEmpty()) {
            ok = false;
            log.error("[" + cms + "] Hiçbir universe'ün tabloları okunamadı. Table mapping'e dokunulmadı.");
        } else {
            try {
                int inserted = tableWriter.replaceSystem(cms, exportDate, tableData, failedTableCuids);
                log.info(String.format("[%s] Table mapping yazıldı: %d satır (%d universe, %d tablo, hatalı %d)",
                        cms, inserted, tableData.size(), tableCount, failedTableCuids.size()));
            } catch (Exception e) {
                ok = false;
                log.error("[" + cms + "] Table mapping DB'ye yazılamadı (eski veri korundu)", e);
            }
        }

        log.info(String.format("[%s] Süre: %d sn", cms, (System.currentTimeMillis() - start) / 1000));
        return ok;
    }

    /** Geçici hatalarda (lisans dolu, ağ vb.) LOGON_ATTEMPTS kez, LOGON_RETRY_WAIT_MS arayla dener. */
    private static BoSession logonWithRetry(String cms, Credential credential, RunLog log) throws Exception {
        for (int attempt = 1; ; attempt++) {
            try {
                return BoSession.logon(cms, credential, AUTH_TYPE);
            } catch (Exception e) {
                if (attempt >= LOGON_ATTEMPTS) {
                    throw e;
                }
                log.warn(String.format("[%s] CMS logon başarısız (deneme %d/%d), %d dk sonra tekrar denenecek: %s",
                        cms, attempt, LOGON_ATTEMPTS, LOGON_RETRY_WAIT_MS / 60000, RunLog.describe(e)));
                Thread.sleep(LOGON_RETRY_WAIT_MS);
            }
        }
    }

    /** Önceki çalıştırmadan kalmış retrieve dosyalarını temizler. */
    private static void cleanDirectory(Path dir, RunLog log) throws IOException {
        Files.createDirectories(dir);
        try (Stream<Path> walk = Files.walk(dir)) {
            walk.sorted(Comparator.reverseOrder())
                .filter(p -> !p.equals(dir))
                .forEach(p -> {
                    if (!p.toFile().delete()) {
                        log.warn("Silinemedi: " + p);
                    }
                });
        }
    }
}
