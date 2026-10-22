package com.akbank.analytics.applications.bo.description;

import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.sdk.BoSession;
import com.sap.sl.sdk.authoring.businesslayer.Attribute;
import com.sap.sl.sdk.authoring.businesslayer.BlContainer;
import com.sap.sl.sdk.authoring.businesslayer.BlItem;
import com.sap.sl.sdk.authoring.businesslayer.BusinessLayer;
import com.sap.sl.sdk.authoring.businesslayer.BusinessObject;
import com.sap.sl.sdk.authoring.businesslayer.Dimension;
import com.sap.sl.sdk.authoring.businesslayer.Measure;
import com.sap.sl.sdk.authoring.cms.CmsResourceService;
import com.sap.sl.sdk.authoring.local.LocalResourceService;
import com.sap.sl.sdk.framework.SlContext;
import com.sap.sl.sdk.framework.cms.CmsSessionService;

import java.io.IOException;
import java.lang.reflect.Method;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.stream.Stream;

/**
 * Tek bir UNX universe'ü indirir (retrieve), business layer'ı açar, kaydeder ve CMS'e geri yayınlar (publish).
 * BoSession'ın İÇİNDE try-with-resources ile kullanılır.
 *
 * Akış: retrieve -> backup (dokunulmamış kopya) -> load -> objelerin açıklamasını değiştir -> save -> close -> publish
 */
public final class UniverseDescriptionEditor implements AutoCloseable {

    private final SlContext context;
    private final CmsResourceService cmsService;
    private final LocalResourceService localService;

    public UniverseDescriptionEditor(BoSession session) {
        this.context = SlContext.create();
        context.getService(CmsSessionService.class).setSession(session.getEnterpriseSession());
        this.cmsService = context.getService(CmsResourceService.class);
        this.localService = context.getService(LocalResourceService.class);
    }

    /** Universe'ü dir klasörüne indirir; .blx dosyasının yolunu döndürür. */
    public String retrieve(UniverseInfo universe, Path dir) throws IOException {
        Files.createDirectories(dir);
        return cmsService.retrieveUniverse(universe.getCmsPath(), dir.toString(), true);
    }

    public BusinessLayer load(String blxPath) {
        return (BusinessLayer) localService.load(blxPath);
    }

    /** Business layer'ı aynı .blx dosyasının üzerine kaydeder. */
    public void save(BusinessLayer businessLayer, String blxPath) {
        localService.save(businessLayer, blxPath, true);
    }

    public void closeQuietly(BusinessLayer businessLayer) {
        if (businessLayer == null) return;
        try {
            localService.close(businessLayer);
        } catch (Exception e) {
            System.err.println("Business layer kapatılamadı: " + e);
        }
    }

    /** Yerel universe'ü CMS klasörüne yayınlar (aynı adla, üzerine yazar). */
    public void publish(String blxPath, String cmsFolderPath) {
        cmsService.publish(blxPath, cmsFolderPath, true);
    }

    /** Business layer'daki tüm objeler (dimension, measure, attribute), tam yollarıyla. */
    public static List<TargetObject> objects(BusinessLayer businessLayer) {
        List<TargetObject> out = new ArrayList<>();
        collect(businessLayer.getRootFolder().getChildren(), "", out);
        return out;
    }

    private static void collect(List<BlItem> items, String path, List<TargetObject> out) {
        for (BlItem item : items) {
            // Aynı öğe hem obje hem kapsayıcı olabilir (ör. altında attribute olan dimension)
            if (item instanceof BusinessObject) {
                String full = path.isEmpty() ? item.getName() : path + "\\" + item.getName();
                out.add(new SdkObject((BusinessObject) item, full));
            }
            if (item instanceof BlContainer) {
                String childPath = path.isEmpty() ? item.getName() : path + "\\" + item.getName();
                collect(((BlContainer) item).getChildren(), childPath, out);
            }
        }
    }

    /**
     * Business layer'da "ad + değer" çiftlerinden oluşan listelerde (query script parametreleri, custom property'ler vb.)
     * aynı adla tekrar eden kayıtları tekilleştirir: ilki kalır, tekrarlar silinir.
     * Ör. IDT'de Query Options'taki CHECK_INTEGRITY parametresi iki kez tanımlıysa SDK universe'ü
     * kaydetmiyor: "The query script property CHECK_INTEGRITY already exists" (SLS 12077).
     *
     * SDK sürümleri arasında metot/tip adları farklı olabildiği için yansıma (reflection) ile, business layer'ın
     * parametresiz ve List döndüren tüm get...() metotları taranır; sadece elemanlarında hem getName() hem
     * getValue() olan listelere dokunulur (iş objeleri vb. etkilenmez). Ad karşılaştırması harf duyarsız.
     *
     * @return silinen (dryRun'da silinecek) tekrarlar: "metot: AD = DEĞER"
     */
    public static List<String> removeDuplicateProperties(Object item, boolean dryRun) throws Exception {
        List<String> removed = new ArrayList<>();
        for (Map.Entry<String, List<?>> e : propertyLists(item).entrySet()) {
            List<?> list = e.getValue();
            Set<String> seen = new HashSet<>();
            List<Object> duplicates = new ArrayList<>();
            for (Object p : list) {
                String name = String.valueOf(call(p, "getName"));
                if (!seen.add(name.toUpperCase(java.util.Locale.ROOT))) {
                    duplicates.add(p);
                    removed.add(e.getKey() + ": " + name + " = " + call(p, "getValue"));
                }
            }
            if (!dryRun) {
                for (Object d : duplicates) {
                    list.remove(d);
                }
            }
        }
        return removed;
    }

    /** item'ın "ad + değer" listeleri: metot adı -> liste (teşhis ve tekilleştirme için). */
    public static Map<String, List<?>> propertyLists(Object item) {
        Map<String, List<?>> out = new TreeMap<>();
        for (Method m : item.getClass().getMethods()) {
            if (m.getParameterCount() != 0 || !m.getName().startsWith("get")
                    || !List.class.isAssignableFrom(m.getReturnType())) {
                continue;
            }
            try {
                m.setAccessible(true);
                Object r = m.invoke(item);
                if (!(r instanceof List) || ((List<?>) r).isEmpty()) continue;
                Object first = ((List<?>) r).get(0);
                if (hasGetter(first, "getName") && hasGetter(first, "getValue")) {
                    out.put(m.getName(), (List<?>) r);
                }
            } catch (Exception ignored) {
                // erişilemeyen metot: atla
            }
        }
        return out;
    }

    private static boolean hasGetter(Object o, String name) {
        try {
            return o != null && o.getClass().getMethod(name).getParameterCount() == 0;
        } catch (NoSuchMethodException e) {
            return false;
        }
    }

    private static Object call(Object target, String method) throws Exception {
        Method m = target.getClass().getMethod(method);
        m.setAccessible(true);   // gerçekleme sınıfı public olmayabilir
        return m.invoke(target);
    }

    /** Dokunulmamış universe dosyalarının kopyası (geri dönüş için). */
    public static void copyDirectory(Path from, Path to) throws IOException {
        try (Stream<Path> walk = Files.walk(from)) {
            for (Path p : (Iterable<Path>) walk::iterator) {
                Path target = to.resolve(from.relativize(p).toString());
                if (Files.isDirectory(p)) {
                    Files.createDirectories(target);
                } else {
                    Files.copy(p, target, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.COPY_ATTRIBUTES);
                }
            }
        }
    }

    @Override
    public void close() {
        try {
            context.close();
        } catch (Exception e) {
            System.err.println("SlContext kapatılamadı: " + e);
        }
    }

    /** SDK objesini TargetObject olarak sarmalar. */
    private static final class SdkObject implements TargetObject {
        private final BusinessObject bo;
        private final String fullPath;

        SdkObject(BusinessObject bo, String fullPath) {
            this.bo = bo;
            this.fullPath = fullPath;
        }

        @Override public String getFullPath()    { return fullPath; }
        @Override public String getDescription() { return bo.getDescription(); }
        @Override public void setDescription(String d) { bo.setDescription(d); }

        @Override
        public String getType() {
            if (bo instanceof Dimension) return "DIMENSION";
            if (bo instanceof Measure)   return "MEASURE";
            if (bo instanceof Attribute) return "ATTRIBUTE";
            return bo.getClass().getSimpleName().toUpperCase();
        }
    }
}
