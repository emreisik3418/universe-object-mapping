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
        // Parametreler: <credentials.json> ["universe adı veya içinde geçen metin"] [--env HOST:6400]
        List<String> pos = new java.util.ArrayList<>();
        String envArg = null;
        for (int i = 0; i < args.length; i++) {
            if ("--env".equalsIgnoreCase(args[i]) && i + 1 < args.length) envArg = args[++i];
            else pos.add(args[i]);
        }
        if (pos.isEmpty()) {
            System.out.println("Kullanım: UniverseReadCheck <credentials.json> [\"universe adı\"] [--env HOST:6400]");
            return;
        }
        String filter = pos.size() > 1 ? pos.get(1) : null;

        AppConfig config = AppConfig.load(Paths.get(pos.get(0)));
        SboxCredentialProvider sbox = new SboxCredentialProvider(
                config.getSboxEndpoint(), config.getSboxUsername(), config.getSboxPassword());

        BoEnvironment env = config.getEnvironments().get(0);
        if (envArg != null) {
            env = null;
            for (BoEnvironment e : config.getEnvironments()) {
                if (e.getCms().equalsIgnoreCase(envArg.trim())) env = e;
            }
            if (env == null) {
                System.out.println("[HATA] --env " + envArg + " credentials.json'da yok.");
                return;
            }
        }
        if (pos.size() > 2) {
            System.out.println("[UYARI] Fazladan parametre var (boşluklu adı \"tırnak\" içine alın): " + pos.subList(2, pos.size()));
        }
        Path workDir = Paths.get(System.getProperty("user.dir"), "retrieve");
        System.out.println("=== " + env.getCms() + " ===");

        try (BoSession session = BoSession.logon(env.getCms(), sbox.get(env.getSbox()), "secEnterprise");
             UniverseReader reader = new UniverseReader(session, workDir)) {

            List<UniverseInfo> all = UniverseCatalog.listUnx(session.getInfoStore());
            System.out.println("UNX sayısı: " + all.size() + "   Aranan: " + (filter == null ? "(ilk universe)" : "[" + filter + "]"));
            UniverseInfo target = pick(all, filter);
            if (target == null) {
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
            // Alias'lar ayrıca: TABLE_NAME = "Alias.<ad>", DERIVED_SCRIPT = asıl tablo
            java.util.List<com.akbank.analytics.applications.bo.model.UniverseTable> aliases = new java.util.ArrayList<>();
            for (com.akbank.analytics.applications.bo.model.UniverseTable t : content.getTables()) {
                if (t.getName().startsWith(com.akbank.analytics.applications.bo.model.UniverseTable.ALIAS_PREFIX)) aliases.add(t);
            }
            System.out.println("[OK]   Alias sayısı: " + aliases.size());
            for (int k = 0; k < Math.min(30, aliases.size()); k++) {
                System.out.println("       A: " + aliases.get(k));
            }
        }
        System.out.println("[OK]   Kapatıldı ve logoff yapıldı.");
    }

    /**
     * Ad eşleştirme: önce tam ad, sonra "içinde geçen". Birden fazla aday varsa listeler.
     * Türkçe karakterler, büyük/küçük harf, tire çeşitleri, fazla boşluk ve ".unx" fark etmez.
     */
    static UniverseInfo pick(List<UniverseInfo> all, String filter) {
        if (filter == null) return all.isEmpty() ? null : all.get(0);
        String f = fold(filter);
        List<UniverseInfo> contains = new java.util.ArrayList<>();
        for (UniverseInfo u : all) {
            String n = fold(u.getName());
            if (n.equals(f)) {
                System.out.println("[OK]   Tam eşleşme.");
                return u;
            }
            if (n.contains(f)) contains.add(u);
        }
        if (contains.size() == 1) return contains.get(0);
        if (contains.size() > 1) {
            System.out.println("[UYARI] " + contains.size() + " universe eşleşti, ilki okunacak. Tam adı yazarak seçin:");
            for (int i = 0; i < Math.min(20, contains.size()); i++) System.out.println("         " + contains.get(i).getName());
            return contains.get(0);
        }
        System.out.println("[HATA] Eşleşen universe bulunamadı. Bu ortamda ilk kelimeyi içerenler:");
        String first = f.split(" ")[0];
        int n = 0;
        for (UniverseInfo u : all) {
            if (fold(u.getName()).contains(first) && n++ < 20) System.out.println("         " + u.getName());
        }
        if (n == 0) System.out.println("         (yok — universe başka bir ortamda olabilir, --env ile ortam seçin)");
        return null;
    }

    /** Karşılaştırma anahtarı: küçük harf, Türkçe karakterler ASCII, tireler '-', tek boşluk, ".unx" yok. */
    static String fold(String s) {
        String t = s.trim().toLowerCase(java.util.Locale.ROOT);
        if (t.endsWith(".unx")) t = t.substring(0, t.length() - 4);
        StringBuilder sb = new StringBuilder(t.length());
        for (char c : t.toCharArray()) {
            switch (c) {
                case '\u0131': case '\u0130': sb.append('i'); break;   // ı İ
                case '\u00f6': sb.append('o'); break;                  // ö
                case '\u00fc': sb.append('u'); break;                  // ü
                case '\u015f': sb.append('s'); break;                  // ş
                case '\u011f': sb.append('g'); break;                  // ğ
                case '\u00e7': sb.append('c'); break;                  // ç
                case '\u2013': case '\u2014': case '\u2212': sb.append('-'); break;  // – — −
                case '\u0307': break;                                 // "İ".toLowerCase() artığı
                default: sb.append(c);
            }
        }
        return sb.toString().replaceAll("\\s+", " ");
    }
}
