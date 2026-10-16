package com.akbank.analytics.applications.bo.model;

/** Bir universe nesnesi (dimension, measure, attribute...). */
public final class UniverseObject {
    private final String id;
    private final String name;
    private final String path;     // business layer içindeki klasör yolu
    private final String type;     // DIMENSION, MEASURE, ATTRIBUTE...
    private final String select;
    private final String where;
    private final String state;

    public UniverseObject(String id, String name, String path, String type,
                          String select, String where, String state) {
        this.id = id;
        this.name = name;
        this.path = path;
        this.type = type;
        this.select = select;
        this.where = where;
        this.state = state;
    }

    public String getId()     { return id; }
    public String getName()   { return name; }
    public String getPath()   { return path; }
    public String getType()   { return type; }
    public String getSelect() { return select; }
    public String getWhere()  { return where; }
    public String getState()  { return state; }

    @Override
    public String toString() {
        return type + " | " + path + " | " + name + " | " + select;
    }
}
