package com.akbank.analytics.applications.bo.model;

/** CMS'teki bir UNX universe'ün kimlik ve konum bilgisi. */
public final class UniverseInfo {
    private final int id;
    private final String cuid;
    private final String name;         // ör. Satis.unx
    private final String folderPath;   // ör. /Universes/AKBANK/AKODE

    public UniverseInfo(int id, String cuid, String name, String folderPath) {
        this.id = id;
        this.cuid = cuid;
        this.name = name;
        this.folderPath = folderPath;
    }

    public int getId()            { return id; }
    public String getCuid()       { return cuid; }
    public String getName()       { return name; }
    public String getFolderPath() { return folderPath; }

    /** SL SDK retrieveUniverse için kullanılacak tam yol. */
    public String getCmsPath()    { return folderPath + "/" + name; }

    @Override
    public String toString() { return getCmsPath() + " [" + cuid + "]"; }
}
