package com.akbank.analytics.applications.bo.sdk;

import com.akbank.analytics.applications.bo.model.UniverseInfo;
import com.crystaldecisions.sdk.exception.SDKException;
import com.crystaldecisions.sdk.occa.infostore.IInfoObject;
import com.crystaldecisions.sdk.occa.infostore.IInfoObjects;
import com.crystaldecisions.sdk.occa.infostore.IInfoStore;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Comparator;
import java.util.Deque;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/** CMS'teki UNX universe'leri klasör yollarıyla birlikte listeler (toplam 2 sorgu). */
public final class UniverseCatalog {

    private static final String FOLDER_QUERY =
            "SELECT TOP 1000000 SI_ID, SI_NAME, SI_PARENTID FROM CI_APPOBJECTS WHERE SI_KIND = 'Folder'";

    // DSL.Universe = UNX universe'ün kendisi (DSL.MetaDataFile değil, o içindeki .blx dosyaları)
    private static final String UNX_QUERY =
            "SELECT TOP 1000000 SI_ID, SI_CUID, SI_NAME, SI_PARENTID FROM CI_APPOBJECTS WHERE SI_KIND = 'DSL.Universe'";

    /** CMS'te universe kök klasörünün sabit ID'si ("Universes"). */
    private static final int UNIVERSE_ROOT_ID = 95;

    private UniverseCatalog() { }

    public static List<UniverseInfo> listUnx(IInfoStore infoStore) throws SDKException {
        // 1) Tüm universe klasörlerini tek sorguda belleğe al
        Map<Integer, Folder> folders = new HashMap<>();
        IInfoObjects folderObjects = infoStore.query(FOLDER_QUERY);
        for (Object o : folderObjects) {
            IInfoObject f = (IInfoObject) o;
            folders.put(f.getID(), new Folder(f.getTitle(), f.getParentID()));
        }

        // 2) Tüm UNX'leri tek sorguda al, yollarını bellekte oluştur
        List<UniverseInfo> result = new ArrayList<>();
        IInfoObjects unxObjects = infoStore.query(UNX_QUERY);
        for (Object o : unxObjects) {
            IInfoObject u = (IInfoObject) o;
            String name = u.getTitle();
            if (!name.toLowerCase().endsWith(".unx")) {
                name = name + ".unx";
            }
            result.add(new UniverseInfo(
                    u.getID(), u.getCUID(), name, folderPath(u.getParentID(), folders)));
        }

        result.sort(Comparator.comparing(UniverseInfo::getCmsPath));
        return result;
    }

    private static String folderPath(int folderId, Map<Integer, Folder> folders) {
        Deque<String> parts = new ArrayDeque<>();
        int current = folderId;
        int guard = 0;                                   // bozuk veride sonsuz döngüye girmesin
        while (current != UNIVERSE_ROOT_ID && folders.containsKey(current) && guard++ < 100) {
            Folder f = folders.get(current);
            parts.addFirst(f.name);
            if ("Universes".equalsIgnoreCase(f.name)) {
                break;                                   // kök klasöre ulaştık, daha yukarı çıkma
            }
            current = f.parentId;
        }
        if (parts.isEmpty() || !"Universes".equalsIgnoreCase(parts.peekFirst())) {
            parts.addFirst("Universes");
        }
        return "/" + String.join("/", parts);
    }

    private static final class Folder {
        final String name;
        final int parentId;
        Folder(String name, int parentId) {
            this.name = name;
            this.parentId = parentId;
        }
    }
}
