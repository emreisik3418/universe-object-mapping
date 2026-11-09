package com.akbank.analytics.applications.bo.model;

import java.util.Objects;

/**
 * Universe'ün data foundation'ındaki bir tablo. TM tablosuna şöyle yazılır:
 *   fiziksel : TABLE_NAME = ŞEMA.TABLO          DERIVED_SCRIPT = boş
 *   derived  : TABLE_NAME = Derived.<ad>        DERIVED_SCRIPT = SQL
 *   alias    : TABLE_NAME = Alias.<alias adı>   DERIVED_SCRIPT = işaret ettiği asıl tablo (ŞEMA.TABLO / Derived.<ad>)
 *              (asıl tablo da ayrıca kendi satırıyla yazılır)
 */
public final class UniverseTable {
    public static final String ALIAS_PREFIX = "Alias.";
    public static final String DERIVED_PREFIX = "Derived.";

    private final String name;           // ŞEMA.TABLO, Derived.<ad> veya Alias.<ad>
    private final String derivedScript;  // derived: SQL, alias: asıl tablo adı

    public UniverseTable(String name, String derivedScript) {
        this.name = name;
        this.derivedScript = derivedScript;
    }

    public String getName()          { return name; }
    public String getDerivedScript() { return derivedScript; }

    @Override
    public boolean equals(Object o) {
        if (this == o) return true;
        if (!(o instanceof UniverseTable)) return false;
        UniverseTable t = (UniverseTable) o;
        return Objects.equals(name, t.name) && Objects.equals(derivedScript, t.derivedScript);
    }

    @Override
    public int hashCode() { return Objects.hash(name, derivedScript); }

    @Override
    public String toString() {
        if (derivedScript == null) return name;
        return name.startsWith(ALIAS_PREFIX) ? name + " -> " + derivedScript : name + " [derived]";
    }
}
