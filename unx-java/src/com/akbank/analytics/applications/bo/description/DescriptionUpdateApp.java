package com.akbank.analytics.applications.bo.description;

import com.akbank.analytics.applications.bo.app.RunLog;
import com.akbank.analytics.applications.bo.config.AppConfig;
import com.akbank.analytics.applications.bo.credential.SboxCredentialProvider;
import com.akbank.analytics.applications.bo.model.BoEnvironment;
import com.akbank.analytics.applications.bo.model.Credential;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.sdk.BoSession;
import com.akbank.analytics.applications.bo.sdk.UniverseCatalog;
import com.sap.sl.sdk.authoring.businesslayer.BusinessLayer;

import java.nio.file.Path;
import java.nio.file.Paths;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Map;

/**
 * Bir UNX universe'ün obje açıklamalarını Oracle'daki bir sorgudan günceller.
 *
 * Kullanım:
 *   DescriptionUpdateApp <credentials.json> <universe> <sorgu.sql> [--env HOST:6400] [--apply]
 *
 *   universe : CMS yolu ("/Universes/AKBANK/Satis.unx", "AKBANK/Satis" de olur) veya CUID
 *   sorgu.sql: OBJECT_PATH ve DESCRIPTION kolonlarını döndüren SELECT
 *   --env    : credentials.json'da birden fazla ortam varsa hangisi
 *   --apply  : verilmezse DRY-RUN — universe'e HİÇ dokunulmaz, sadece rapor yazılır
 *
 * Güvenlik:
 *   - Varsayılan dry-run. Her çalıştırmada reports\ altına ne yapılacağını gösteren CSV yazılır.
 *   - --apply'da yayınlamadan önce CMS'teki universe'ün dokunulmamış kopyası backup\ altına alınır.
 *   - Sadece açıklaması FARKLI olan objeler değişir; DB'de boş açıklama mevcut açıklamayı silmez.
 *   - Yayından sonra universe tekrar indirilip açıklamalar doğrulanır; CUID'in değişmediği kontrol edilir.
 *
 * Çıkış kodu: 0 = başarılı, 1 = yayınlandı ama doğrulamada sorun / uyarı, 2 = hata (yayın yapılmadı).
 */
public final class DescriptionUpdateApp {

    private static final String AUTH_TYPE = System.getProperty("bo.authType", "secEnterprise");
    private static final int LOGON_ATTEMPTS = 4;
    private static final long LOGON_RETRY_WAIT_MS = 2 * 60 * 1000L;

    private static final String USAGE =
            "Kullanım: DescriptionUpdateApp <credentials.json> <universe yolu veya CUID> <sorgu.sql> [--env HOST:6400] [--apply]";

    public static void main(String[] args) {
        int exitCode;
        RunLog log = null;
        try {
            Path base = Paths.get(System.getProperty("user.dir"));
            log = RunLog.open(base.resolve("logs"), "description");
            exitCode = run(args, base, log);
        } catch (Throwable t) {
            if (log != null) log.error("Beklenmeyen hata, uygulama durduruldu", t);
            else t.printStackTrace();
            exitCode = 2;
        } finally {
            if (log != null) log.close();
        }
        System.exit(exitCode);   // BO SDK'nın arka plan thread'leri JVM'i açık tutmasın
    }

    private static int run(String[] args, Path base, RunLog log) throws Exception {
        // ---- Parametreler
        List<String> positional = new ArrayList<>();
        String envArg = null;
        boolean apply = false;
        for (int i = 0; i < args.length; i++) {
            if ("--apply".equalsIgnoreCase(args[i])) apply = true;
            else if ("--env".equalsIgnoreCase(args[i]) && i + 1 < args.length) envArg = args[++i];
            else positional.add(args[i]);
        }
        if (positional.size() != 3) {
            log.error(USAGE);
            log.error("3 parametre bekleniyordu, " + positional.size() + " geldi. Boşluk içeren değerleri \"tırnak\" içine alın:");
            for (int i = 0; i < positional.size(); i++) {
                log.error("  [" + (i + 1) + "] " + positional.get(i));
            }
            return 2;
        }
        String universeArg = positional.get(1);
        Path sqlFile = Paths.get(positional.get(2));
        String stamp = new SimpleDateFormat("yyyyMMdd_HHmmss").format(new Date());

        log.info("Başladı. Mod: " + (apply ? "APPLY (universe güncellenecek ve yayınlanacak)" : "DRY-RUN (hiçbir şey değiştirilmeyecek)"));
        log.info("Log: " + log.getPath());

        AppConfig config = AppConfig.load(Paths.get(positional.get(0)));
        BoEnvironment env = pickEnvironment(config, envArg);
        if (env == null) {
            log.error("Ortam seçilemedi. credentials.json'daki ortamlar: " + cmsList(config) + ". --env ile belirtin.");
            return 2;
        }
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        // ---- 1) DB'den açıklamalar (önce bu: sorgu hatalıysa BO'ya hiç bağlanmayalım)
        String sql = DescriptionSource.loadSql(sqlFile);
        List<DescriptionRow> rows = new DescriptionSource(config.getDbJdbcUrl(), sbox.get(config.getDbTarget())).read(sql);
        log.info("DB'den gelen satır: " + rows.size() + "  (sorgu: " + sqlFile.toAbsolutePath() + ")");
        if (rows.isEmpty()) {
            log.error("Sorgu hiç satır döndürmedi. Universe'e dokunulmadı.");
            return 2;
        }

        String cms = env.getCms();
        Credential boCredential = sbox.get(env.getSbox());

        try (BoSession session = logonWithRetry(cms, boCredential, log);
             UniverseDescriptionEditor editor = new UniverseDescriptionEditor(session)) {

            // ---- 2) Universe'ü bul
            List<UniverseInfo> all = UniverseCatalog.listUnx(session.getInfoStore());
            UniverseInfo universe = find(all, universeArg);
            if (universe == null) {
                log.error("[" + cms + "] Universe bulunamadı: " + universeArg + "  (CMS yolu veya CUID verin)");
                return 2;
            }
            log.info("[" + cms + "] Universe: " + universe);
            String safeName = universe.getName().replaceAll("[^A-Za-z0-9._-]", "_");

            // ---- 3) İndir + dokunulmamış yedek
            Path workDir = base.resolve("description").resolve(safeName + "_" + stamp);
            Path retrieveDir = workDir.resolve("retrieve");
            String blxPath = editor.retrieve(universe, retrieveDir);
            Path backupDir = base.resolve("backup").resolve(safeName + "_" + stamp);
            UniverseDescriptionEditor.copyDirectory(retrieveDir, backupDir);
            log.info("İndirildi. Dokunulmamış yedek: " + backupDir);

            // ---- 4) Eşleştir, raporla
            BusinessLayer bl = null;
            int changed;
            try {
                bl = editor.load(blxPath);
                List<TargetObject> objects = UniverseDescriptionEditor.objects(bl);
                DescriptionPlan plan = DescriptionPlan.build(objects, rows);

                Path report = base.resolve("reports").resolve("description_" + safeName + "_" + stamp + ".csv");
                DescriptionReport.write(plan, report);
                log.info("Universe'teki obje: " + objects.size() + "   Rapor: " + report);
                for (Map.Entry<DescriptionPlan.Status, Integer> c : plan.getCounts().entrySet()) {
                    log.info(String.format("  %-15s %d", c.getKey(), c.getValue()));
                }

                int toUpdate = plan.count(DescriptionPlan.Status.UPDATE);
                if (!apply) {
                    log.info("DRY-RUN: universe'e dokunulmadı. " + toUpdate +
                             " obje güncellenecek. Raporu kontrol edip --apply ile çalıştırın.");
                    return 0;
                }
                if (toUpdate == 0) {
                    log.info("Güncellenecek obje yok. Yayın yapılmadı.");
                    return 0;
                }

                // ---- 5) Uygula + kaydet
                changed = plan.apply();
                editor.save(bl, blxPath);
                log.info(changed + " objenin açıklaması değiştirildi ve yerel kopya kaydedildi.");
            } finally {
                editor.closeQuietly(bl);
            }

            // ---- 6) Yayınla
            log.info("Yayınlanıyor: " + universe.getFolderPath());
            editor.publish(blxPath, universe.getFolderPath());
            log.info("Yayınlandı.");

            // ---- 7) Doğrula: CUID aynı mı, açıklamalar CMS'te gerçekten değişmiş mi
            return verify(session, editor, universe, rows, workDir, changed, backupDir, log);
        }
    }

    private static int verify(BoSession session, UniverseDescriptionEditor editor, UniverseInfo universe,
                              List<DescriptionRow> rows, Path workDir, int changed, Path backupDir,
                              RunLog log) throws Exception {
        boolean ok = true;

        UniverseInfo after = find(UniverseCatalog.listUnx(session.getInfoStore()), universe.getCmsPath());
        if (after == null) {
            log.error("DOĞRULAMA: Yayından sonra universe CMS'te bulunamadı! Yedek: " + backupDir);
            return 1;
        }
        if (!after.getCuid().equals(universe.getCuid())) {
            ok = false;
            log.warn("DOĞRULAMA: CUID değişti (" + universe.getCuid() + " -> " + after.getCuid() +
                     "). Bu universe'ü kullanan raporlar etkilenebilir!");
        }

        String blx = editor.retrieve(after, workDir.resolve("verify"));
        BusinessLayer bl = null;
        try {
            bl = editor.load(blx);
            DescriptionPlan check = DescriptionPlan.build(UniverseDescriptionEditor.objects(bl), rows);
            int remaining = check.count(DescriptionPlan.Status.UPDATE);
            if (remaining == 0) {
                log.info("DOĞRULAMA OK: " + changed + " açıklama CMS'teki universe'te güncel.");
            } else {
                ok = false;
                log.warn("DOĞRULAMA: " + remaining + " obje hâlâ güncel değil. Yedek: " + backupDir);
            }
        } finally {
            editor.closeQuietly(bl);
        }
        log.info("Bitti. Sonuç: " + (ok ? "BAŞARILI" : "UYARILI (detaylar yukarıda)"));
        return ok ? 0 : 1;
    }

    /** CUID, tam CMS yolu veya "/Universes" ve ".unx" olmadan kısa yol ile arar (harf duyarsız). */
    static UniverseInfo find(List<UniverseInfo> all, String arg) {
        String a = arg.trim().replace('\\', '/');
        for (UniverseInfo u : all) {
            if (u.getCuid() != null && u.getCuid().equals(a)) return u;
        }
        String wanted = shortPath(a);
        for (UniverseInfo u : all) {
            if (shortPath(u.getCmsPath()).equalsIgnoreCase(wanted)) return u;
        }
        return null;
    }

    private static String shortPath(String p) {
        String s = p.trim().replace('\\', '/');
        while (s.startsWith("/")) s = s.substring(1);
        if (s.toLowerCase().startsWith("universes/")) s = s.substring("universes/".length());
        if (s.toLowerCase().endsWith(".unx")) s = s.substring(0, s.length() - 4);
        return s;
    }

    private static BoEnvironment pickEnvironment(AppConfig config, String envArg) {
        if (envArg == null) {
            return config.getEnvironments().size() == 1 ? config.getEnvironments().get(0) : null;
        }
        for (BoEnvironment e : config.getEnvironments()) {
            if (e.getCms().equalsIgnoreCase(envArg.trim())) return e;
        }
        return null;
    }

    private static String cmsList(AppConfig config) {
        List<String> l = new ArrayList<>();
        for (BoEnvironment e : config.getEnvironments()) l.add(e.getCms());
        return String.join(", ", l);
    }

    /** Geçici hatalarda (lisans dolu, ağ vb.) LOGON_ATTEMPTS kez, LOGON_RETRY_WAIT_MS arayla dener. */
    private static BoSession logonWithRetry(String cms, Credential credential, RunLog log) throws Exception {
        for (int attempt = 1; ; attempt++) {
            try {
                return BoSession.logon(cms, credential, AUTH_TYPE);
            } catch (Exception e) {
                if (attempt >= LOGON_ATTEMPTS) throw e;
                log.warn(String.format("[%s] CMS logon başarısız (deneme %d/%d), %d dk sonra tekrar denenecek: %s",
                        cms, attempt, LOGON_ATTEMPTS, LOGON_RETRY_WAIT_MS / 60000, RunLog.describe(e)));
                Thread.sleep(LOGON_RETRY_WAIT_MS);
            }
        }
    }
}
