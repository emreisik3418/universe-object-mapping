package com.akbank.analytics.applications.bo.model;

import java.util.Objects;

/** Universe'ün data foundation'ındaki bir tablo (fiziksel, alias çözülmüş veya derived). */
public final class UniverseTable {
    private final String name;           // ŞEMA.TABLO veya Derived.<ad>
    private final String derivedScript;  // sadece derived table için SQL

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
    public String toString() { return derivedScript == null ? name : name + " [derived]"; }
}
