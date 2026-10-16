package com.akbank.analytics.applications.bo.model;

import java.util.List;

/** Tek bir universe'ten okunan her şey. tableError doluysa tablolar okunamamıştır (nesneler geçerlidir). */
public final class UniverseContent {
    private final List<UniverseObject> objects;
    private final List<UniverseTable> tables;
    private final Exception tableError;

    public UniverseContent(List<UniverseObject> objects, List<UniverseTable> tables, Exception tableError) {
        this.objects = objects;
        this.tables = tables;
        this.tableError = tableError;
    }

    public List<UniverseObject> getObjects() { return objects; }
    public List<UniverseTable> getTables()   { return tables; }
    public Exception getTableError()         { return tableError; }
}
