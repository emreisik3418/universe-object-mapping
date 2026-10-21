package com.akbank.analytics.applications.bo.description;

import java.util.ArrayList;
import java.util.Collections;
import java.util.EnumMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * DB satırlarını universe objeleriyle obje yolu üzerinden eşleştirir ve ne yapılacağını hesaplar.
 * Hiçbir şeyi değiştirmez; değişiklik sadece apply() ile ve sadece UPDATE durumundakilere uygulanır.
 *
 * Yol karşılaştırması:
 *  - "/" ve "\" aynı kabul edilir, ayraçların etrafındaki boşluklar ve baştaki/sondaki ayraç yok sayılır.
 *  - Önce birebir eşleşme aranır; yoksa büyük/küçük harf duyarsız (Türkçe I/İ/ı/i dahil) tekil eşleşme.
 */
public final class DescriptionPlan {

    public enum Status {
        /** Açıklama değişecek. */
        UPDATE,
        /** Açıklama zaten aynı. */
        SAME,
        /** DB'deki açıklama boş: mevcut açıklama silinmez, atlanır. */
        EMPTY_IN_DB,
        /** DB'deki yol universe'te bulunamadı. */
        NOT_FOUND,
        /** Aynı yol DB'de farklı açıklamalarla birden fazla kez var: hangisi doğru bilinmediği için atlanır. */
        CONFLICT_IN_DB,
        /** Harf duyarsız eşleşmede birden fazla obje çıktı: atlanır. */
        AMBIGUOUS,
        /** Universe'te var, DB sorgusunda yok (bilgi amaçlı, dokunulmaz). */
        NOT_IN_DB
    }

    public static final class Item {
        private final Status status;
        private final String dbPath;
        private final TargetObject target;
        private final String currentDescription;
        private final String newDescription;

        Item(Status status, String dbPath, TargetObject target, String currentDescription, String newDescription) {
            this.status = status;
            this.dbPath = dbPath;
            this.target = target;
            this.currentDescription = currentDescription;
            this.newDescription = newDescription;
        }

        public Status getStatus()             { return status; }
        public String getDbPath()             { return dbPath; }
        public TargetObject getTarget()       { return target; }
        public String getCurrentDescription() { return currentDescription; }
        public String getNewDescription()     { return newDescription; }

        /** Rapor için gösterilecek yol: universe'teki gerçek yol, yoksa DB'deki. */
        public String getPath() { return target != null ? target.getFullPath() : dbPath; }
    }

    private final List<Item> items;
    private final Map<Status, Integer> counts;

    private DescriptionPlan(List<Item> items) {
        this.items = Collections.unmodifiableList(items);
        Map<Status, Integer> c = new EnumMap<>(Status.class);
        for (Status s : Status.values()) c.put(s, 0);
        for (Item i : items) c.put(i.status, c.get(i.status) + 1);
        this.counts = Collections.unmodifiableMap(c);
    }

    public List<Item> getItems()            { return items; }
    public int count(Status s)              { return counts.get(s); }
    public Map<Status, Integer> getCounts() { return counts; }

    public static DescriptionPlan build(List<TargetObject> objects, List<DescriptionRow> rows) {
        // Universe objeleri: birebir ve harf duyarsız indeksler
        Map<String, List<TargetObject>> exact = new LinkedHashMap<>();
        Map<String, List<TargetObject>> folded = new LinkedHashMap<>();
        for (TargetObject o : objects) {
            String key = normalizePath(o.getFullPath());
            exact.computeIfAbsent(key, k -> new ArrayList<>()).add(o);
            folded.computeIfAbsent(fold(key), k -> new ArrayList<>()).add(o);
        }

        // DB satırları: aynı yol birden fazla kez gelirse grupla
        Map<String, List<DescriptionRow>> byPath = new LinkedHashMap<>();
        for (DescriptionRow r : rows) {
            if (r.getObjectPath() == null || r.getObjectPath().trim().isEmpty()) continue;
            byPath.computeIfAbsent(normalizePath(r.getObjectPath()), k -> new ArrayList<>()).add(r);
        }

        List<Item> items = new ArrayList<>();
        Set<TargetObject> touched = new HashSet<>();

        for (Map.Entry<String, List<DescriptionRow>> e : byPath.entrySet()) {
            String dbPath = e.getValue().get(0).getObjectPath().trim();

            Set<String> distinct = new HashSet<>();
            for (DescriptionRow r : e.getValue()) distinct.add(normalizeText(r.getDescription()));
            String newDesc = distinct.size() == 1 ? distinct.iterator().next() : null;

            List<TargetObject> matches = exact.get(e.getKey());
            if (matches == null) matches = folded.get(fold(e.getKey()));

            if (matches == null || matches.isEmpty()) {
                items.add(new Item(Status.NOT_FOUND, dbPath, null, null, newDesc));
                continue;
            }
            if (matches.size() > 1) {
                for (TargetObject t : matches) touched.add(t);
                items.add(new Item(Status.AMBIGUOUS, dbPath, null, null, newDesc));
                continue;
            }

            TargetObject target = matches.get(0);
            touched.add(target);
            String current = normalizeText(target.getDescription());

            Status status;
            if (distinct.size() > 1)       status = Status.CONFLICT_IN_DB;
            else if (newDesc.isEmpty())    status = Status.EMPTY_IN_DB;
            else if (newDesc.equals(current)) status = Status.SAME;
            else                           status = Status.UPDATE;
            items.add(new Item(status, dbPath, target, current, newDesc));
        }

        for (TargetObject o : objects) {
            if (!touched.contains(o)) {
                items.add(new Item(Status.NOT_IN_DB, null, o, normalizeText(o.getDescription()), null));
            }
        }
        return new DescriptionPlan(items);
    }

    /** UPDATE durumundaki objelerin açıklamasını değiştirir (sadece bellekteki business layer). */
    public int apply() {
        int n = 0;
        for (Item i : items) {
            if (i.status == Status.UPDATE) {
                i.target.setDescription(i.newDescription);
                n++;
            }
        }
        return n;
    }

    /** "/Klasör / Alt\Obje/" -> "Klasör\Alt\Obje" */
    static String normalizePath(String path) {
        String[] parts = path.replace('/', '\\').split("\\\\");
        StringBuilder sb = new StringBuilder();
        for (String p : parts) {
            String t = p.trim();
            if (t.isEmpty()) continue;
            if (sb.length() > 0) sb.append('\\');
            sb.append(t);
        }
        return sb.toString();
    }

    /** Harf duyarsız karşılaştırma anahtarı; Türkçe I/İ/ı/i'yi de eşit sayar. */
    static String fold(String s) {
        StringBuilder sb = new StringBuilder(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = Character.toLowerCase(Character.toUpperCase(s.charAt(i)));
            if (c == 'ı') c = 'i';   // ı -> i
            sb.append(c);
        }
        return sb.toString();
    }

    /** Açıklama karşılaştırması: null = "", satır sonları \n, baştaki/sondaki boşluklar yok. */
    static String normalizeText(String s) {
        return s == null ? "" : s.replace("\r\n", "\n").replace('\r', '\n').trim();
    }
}
