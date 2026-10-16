package com.akbank.analytics.applications.bo.sdk;

import com.akbank.analytics.applications.bo.model.UniverseContent;
import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.akbank.analytics.applications.bo.model.UniverseObject;
import com.akbank.analytics.applications.bo.model.UniverseTable;
import com.sap.sl.sdk.authoring.businesslayer.Attribute;
import com.sap.sl.sdk.authoring.businesslayer.BlContainer;
import com.sap.sl.sdk.authoring.businesslayer.BlItem;
import com.sap.sl.sdk.authoring.businesslayer.BusinessLayer;
import com.sap.sl.sdk.authoring.businesslayer.BusinessObject;
import com.sap.sl.sdk.authoring.businesslayer.Dimension;
import com.sap.sl.sdk.authoring.businesslayer.Measure;
import com.sap.sl.sdk.authoring.businesslayer.RelationalBusinessLayer;
import com.sap.sl.sdk.authoring.cms.CmsResourceService;
import com.sap.sl.sdk.authoring.datafoundation.AliasTable;
import com.sap.sl.sdk.authoring.datafoundation.DataFoundation;
import com.sap.sl.sdk.authoring.datafoundation.DataFoundationService;
import com.sap.sl.sdk.authoring.datafoundation.DerivedTable;
import com.sap.sl.sdk.authoring.datafoundation.Table;
import com.sap.sl.sdk.authoring.local.LocalResourceService;
import com.sap.sl.sdk.framework.SlContext;
import com.sap.sl.sdk.framework.cms.CmsSessionService;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;
import java.util.stream.Stream;

/**
 * Bir CMS oturumu üzerinde UNX universe'leri okur: nesneler (business layer) + tablolar (data foundation).
 * Universe tek kez retrieve edilir. BoSession'ın İÇİNDE try-with-resources ile kullanılır.
 */
public final class UniverseReader implements AutoCloseable {

    private final SlContext context;
    private final CmsResourceService cmsService;
    private final LocalResourceService localService;
    private final DataFoundationService dfService;
    private final Path workDir;

    public UniverseReader(BoSession session, Path workDir) throws IOException {
        Files.createDirectories(workDir);
        this.workDir = workDir;
        this.context = SlContext.create();
        context.getService(CmsSessionService.class).setSession(session.getEnterpriseSession());
        this.cmsService = context.getService(CmsResourceService.class);
        this.localService = context.getService(LocalResourceService.class);
        this.dfService = context.getService(DataFoundationService.class);
    }

    /** Universe'ü indirir, nesnelerini ve tablolarını okur, kaynakları bırakır ve yerel dosyaları siler. */
    public UniverseContent read(UniverseInfo universe) throws Exception {
        Path dir = Files.createTempDirectory(workDir, "unx_");
        BusinessLayer businessLayer = null;
        DataFoundation dataFoundation = null;
        try {
            String blxPath = cmsService.retrieveUniverse(universe.getCmsPath(), dir.toString(), true);
            businessLayer = (BusinessLayer) localService.load(blxPath);

            // 1) Nesneler
            List<UniverseObject> objects = new ArrayList<>();
            collect(businessLayer.getRootFolder().getChildren(), "", objects);

            // 2) Tablolar (hata olursa nesneler yine geçerli kalır)
            List<UniverseTable> tables = new ArrayList<>();
            Exception tableError = null;
            if (businessLayer instanceof RelationalBusinessLayer) {
                try {
                    String dfxPath = ((RelationalBusinessLayer) businessLayer).getDataFoundationPath();
                    dataFoundation = (DataFoundation) localService.load(dfxPath);
                    Set<UniverseTable> unique = new LinkedHashSet<>();
                    for (Table t : dataFoundation.getTables()) {
                        unique.add(toUniverseTable(t, 0));
                    }
                    tables.addAll(unique);
                } catch (Exception e) {
                    tableError = e;
                }
            }
            return new UniverseContent(objects, tables, tableError);
        } finally {
            if (dataFoundation != null) {
                try { localService.close(dataFoundation); }
                catch (Exception e) { System.err.println("Data foundation kapatılamadı: " + universe.getCmsPath() + " - " + e); }
            }
            if (businessLayer != null) {
                try { localService.close(businessLayer); }
                catch (Exception e) { System.err.println("Business layer kapatılamadı: " + universe.getCmsPath() + " - " + e); }
            }
            deleteQuietly(dir);
        }
    }

    /** Eski uygulamayla aynı kurallar: derived -> "Derived.<ad>" + SQL, alias -> asıl tablo, fiziksel -> ŞEMA.TABLO */
    private UniverseTable toUniverseTable(Table table, int depth) {
        if (table instanceof DerivedTable) {
            DerivedTable d = (DerivedTable) table;
            return new UniverseTable("Derived." + d.getName(), clean(d.getExpression()));
        }
        if (table instanceof AliasTable && depth < 20) {     // alias zincirinde sonsuz döngüye girmesin
            Table aliased = ((AliasTable) table).getAliasedTable();
            if (aliased != null) {
                return toUniverseTable(aliased, depth + 1);
            }
        }
        return new UniverseTable(physicalName(dfService.getTableFullName(table)), null);
    }

    private String physicalName(String fullName) {
        String[] parts = dfService.splitTableFullName(fullName);
        if (parts != null && parts.length >= 3) {
            return parts[1] + "." + parts[2];
        }
        return fullName;
    }

    @SuppressWarnings("deprecation")
    private static void collect(List<BlItem> items, String path, List<UniverseObject> out) {
        for (BlItem item : items) {
            // Aynı öğe hem nesne hem kapsayıcı olabilir (ör. altında attribute olan dimension)
            if (item instanceof BusinessObject) {
                BusinessObject bo = (BusinessObject) item;
                out.add(new UniverseObject(
                        bo.getIdentifier(),
                        bo.getName(),
                        path,
                        typeOf(bo),
                        clean(bo.getSelect()),
                        clean(bo.getWhere()),
                        bo.getState() != null ? bo.getState().getName() : null));
            }
            if (item instanceof BlContainer) {
                String childPath = path.isEmpty() ? item.getName() : path + "\\" + item.getName();
                collect(((BlContainer) item).getChildren(), childPath, out);
            }
        }
    }

    private static String typeOf(BusinessObject bo) {
        if (bo instanceof Dimension) return "DIMENSION";
        if (bo instanceof Measure)   return "MEASURE";
        if (bo instanceof Attribute) return "ATTRIBUTE";
        return bo.getClass().getSimpleName().toUpperCase();
    }

    private static String clean(String sql) {
        return sql == null ? null : sql.replace("\r\n", " ").replace('\n', ' ').replace('\r', ' ').trim();
    }

    private static void deleteQuietly(Path dir) {
        try (Stream<Path> walk = Files.walk(dir)) {
            walk.sorted(Comparator.reverseOrder()).forEach(p -> p.toFile().delete());
        } catch (IOException e) {
            System.err.println("Geçici klasör silinemedi: " + dir + " - " + e);
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
}
