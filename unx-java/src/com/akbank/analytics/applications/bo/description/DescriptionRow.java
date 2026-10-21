package com.akbank.analytics.applications.bo.description;

/** Oracle sorgusundan gelen bir satır: universe içindeki obje yolu ve yazılacak açıklama. */
public final class DescriptionRow {
    private final String objectPath;
    private final String description;

    public DescriptionRow(String objectPath, String description) {
        this.objectPath = objectPath;
        this.description = description;
    }

    public String getObjectPath()  { return objectPath; }
    public String getDescription() { return description; }
}
