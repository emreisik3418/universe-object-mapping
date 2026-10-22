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
 *   --diag   : teşhis — custom property'leri listeler ve universe'ü DEĞİŞTİRMEDEN yerel bir kopyaya
 *              kaydetmeyi dener (yayın yok). Kaydetme hatalarının universe'ten mi kaynaklandığını gösterir.
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
        boolean diag = false;
        for (int i = 0; i < args.length; i++) {
            if ("--apply".equalsIgnoreCase(args[i])) apply = true;
            else if ("--diag".equalsIgnoreCase(args[i])) diag = true;
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
                if (diag) {
                    return diagnose(editor, bl, workDir, log);
                }
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
                    logDuplicates(UniverseDescriptionEditor.removeDuplicateProperties(bl, true),
                            "Tekrar eden parametre var, --apply'da silinecek (ilki korunur): ", log);
                    log.info("DRY-RUN: universe'e dokunulmadı. " + toUpdate +
                             " obje güncellenecek. Raporu kontrol edip --apply ile çalıştırın.");
                    return 0;
                }
                if (toUpdate == 0) {
                    log.info("Güncellenecek obje yok. Yayın yapılmadı.");
                    return 0;
                }

                // ---- 5) Uygula + kaydet
                // Aynı adla tekrar eden custom property (ör. CHECK_INTEGRITY) varsa SDK kaydetmeyi reddediyor
                logDuplicates(UniverseDescriptionEditor.removeDuplicateProperties(bl, false),
                        "Tekrar eden parametre silindi (ilki korundu): ", log);
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

    /**
     * Teşhis (yayın yok): "ad + değer" listelerini yazar, universe'ü değiştirmeden yerel kopyaya kaydetmeyi dener;
     * olmazsa tekrar eden parametreleri SADECE bellekte tekilleştirip tekrar dener.
     */
    private static int diagnose(UniverseDescriptionEditor editor, BusinessLayer bl, Path workDir, RunLog log)
            throws Exception {
        log.info("TEŞHİS modu: CMS'teki universe değiştirilmeyecek ve yayınlanmayacak.");
        Map<String, List<?>> lists = UniverseDescriptionEditor.propertyLists(bl);
        if (lists.isEmpty()) log.info("Business layer'da ad+değer listesi bulunamadı.");
        for (Map.Entry<String, List<?>> e : lists.entrySet()) {
            log.info("Business layer." + e.getKey() + "(): " + e.getValue().size() + " kayıt");
            for (Object p : e.getValue()) {
                log.info("    " + prop(p, "getName") + " = " + prop(p, "getValue"));
            }
        }

        dumpApi("BL", bl, 0, new java.util.IdentityHashMap<>(), log);

        Path dir = workDir.resolve("diag");
        java.nio.file.Files.createDirectories(dir);
        if (trySave(editor, bl, dir.resolve("1_degisikliksiz.blx"), "DEĞİŞİKLİKSİZ", log)) {
            log.info("  => Universe olduğu gibi kaydedilebiliyor. Bu log'u gönderin.");
            return 0;
        }

        List<String> dupes = UniverseDescriptionEditor.removeDuplicateProperties(bl, false);
        if (dupes.isEmpty()) {
            log.info("  => Tekrar eden parametre bulunamadı; otomatik düzeltme yok. Bu log'u gönderin.");
            return 1;
        }
        for (String d : dupes) log.warn("Bellekte silindi (ilki korundu): " + d);
        if (trySave(editor, bl, dir.resolve("2_tekillestirilmis.blx"), "TEKİLLEŞTİRME SONRASI", log)) {
            log.info("  => ÇÖZÜM BU: --apply aynı tekilleştirmeyi yapıp kaydedecek ve yayınlayacak.");
            return 0;
        }
        log.info("  => Tekilleştirme yetmedi. Bu log'u gönderin.");
        return 1;
    }

    private static final java.util.regex.Pattern INTERESTING =
            java.util.regex.Pattern.compile("(?i).*(param|script|query|option|propert|setting|integrity).*");

    /**
     * Teşhis: nesnenin parametresiz public get/is metotlarını ve dönüş değerlerini yazar.
     * Adında param/script/query/option/property geçen nesnelere 2 seviyeye kadar iner.
     * Liste/Map'lerin boyutunu ve ilk elemanlarını gösterir. Hiçbir şeyi değiştirmez.
     */
    private static void dumpApi(String label, Object o, int depth, java.util.Map<Object, Boolean> seen, RunLog log) {
        if (o == null || seen.put(o, Boolean.TRUE) != null) return;
        String indent = new String(new char[depth * 4]).replace('\0', ' ');
        StringBuilder ifaces = new StringBuilder();
        for (Class<?> c = o.getClass(); c != null; c = c.getSuperclass()) {
            for (Class<?> i : c.getInterfaces()) ifaces.append(i.getSimpleName()).append(' ');
        }
        log.info(indent + "API " + label + " : " + o.getClass().getName() + "  [" + ifaces.toString().trim() + "]");
        java.lang.reflect.Method[] methods = o.getClass().getMethods();
        java.util.Arrays.sort(methods, java.util.Comparator.comparing(java.lang.reflect.Method::getName));
        for (java.lang.reflect.Method m : methods) {
            String n = m.getName();
            if (m.getParameterCount() != 0 || m.getDeclaringClass() == Object.class
                    || !(n.startsWith("get") || n.startsWith("is"))) continue;
            Object v;
            try {
                m.setAccessible(true);
                v = m.invoke(o);
            } catch (Throwable t) {
                log.info(indent + "  " + n + "() -> HATA " + t.getClass().getSimpleName());
                continue;
            }
            log.info(indent + "  " + n + "() : " + m.getReturnType().getSimpleName() + " = " + summary(v));
            boolean interesting = INTERESTING.matcher(n).matches();
            if (depth < 2 && interesting && v != null) {
                if (v instanceof java.util.Collection) {
                    int k = 0;
                    for (Object e : (java.util.Collection<?>) v) {
                        if (k++ >= 5) break;
                        dumpApi(n + "[" + (k - 1) + "]", e, depth + 1, seen, log);
                    }
                } else if (v instanceof java.util.Map) {
                    for (java.util.Map.Entry<?, ?> e : ((java.util.Map<?, ?>) v).entrySet()) {
                        log.info(indent + "      " + e.getKey() + " = " + summary(e.getValue()));
                    }
                } else if (!isSimple(v)) {
                    dumpApi(n, v, depth + 1, seen, log);
                }
            }
        }
    }

    private static boolean isSimple(Object v) {
        return v instanceof CharSequence || v instanceof Number || v instanceof Boolean
                || v instanceof Enum || v.getClass().isPrimitive();
    }

    private static String summary(Object v) {
        if (v == null) return "null";
        if (v instanceof java.util.Collection) {
            java.util.Collection<?> c = (java.util.Collection<?>) v;
            return c.getClass().getSimpleName() + "(" + c.size() + ")" +
                   (c.isEmpty() ? "" : " ilk eleman: " + c.iterator().next().getClass().getName());
        }
        if (v instanceof java.util.Map) return "Map(" + ((java.util.Map<?, ?>) v).size() + ")";
        String s = String.valueOf(v).replace('\n', ' ');
        return s.length() > 100 ? s.substring(0, 100) + "..." : s;
    }

    private static boolean trySave(UniverseDescriptionEditor editor, BusinessLayer bl, Path file,
                                   String label, RunLog log) {
        try {
            editor.save(bl, file.toString());
            log.info("TEŞHİS: " + label + " kaydetme BAŞARILI -> " + file);
            return true;
        } catch (Exception e) {
            log.error("TEŞHİS: " + label + " kaydetme BAŞARISIZ", e);
            return false;
        }
    }

    private static String prop(Object p, String method) {
        try {
            java.lang.reflect.Method m = p.getClass().getMethod(method);
            m.setAccessible(true);
            String v = String.valueOf(m.invoke(p));
            return v.length() > 120 ? v.substring(0, 120) + "..." : v;
        } catch (Exception e) {
            return "?";
        }
    }

    private static void logDuplicates(List<String> dupes, String prefix, RunLog log) {
        for (String d : dupes) log.warn(prefix + d);
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
