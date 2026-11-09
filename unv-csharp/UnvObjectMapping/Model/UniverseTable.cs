using System;

namespace UnvObjectMapping.Model
{
    /// <summary>
    /// Universe'ün kullandığı tablo. TM tablosuna şöyle yazılır:
    ///   fiziksel : TABLE_NAME = ŞEMA.TABLO          DERIVED_SCRIPT = boş
    ///   derived  : TABLE_NAME = Derived.&lt;ad&gt;        DERIVED_SCRIPT = SQL
    ///   alias    : TABLE_NAME = Alias.&lt;alias adı&gt;   DERIVED_SCRIPT = işaret ettiği asıl tablo
    ///              (asıl tablo da ayrıca kendi satırıyla yazılır)
    /// </summary>
    public sealed class UniverseTable : IEquatable<UniverseTable>
    {
        public const string AliasPrefix = "Alias.";

        public UniverseTable(string name, string derivedScript)
        {
            Name = name;
            DerivedScript = derivedScript;
        }

        /// <summary>ŞEMA.TABLO, Derived.&lt;ad&gt; veya Alias.&lt;ad&gt;</summary>
        public string Name { get; }

        /// <summary>Derived: SQL; alias: işaret ettiği asıl tablo adı; fiziksel: null.</summary>
        public string DerivedScript { get; }

        public bool Equals(UniverseTable other) =>
            other != null && Name == other.Name && DerivedScript == other.DerivedScript;

        public override bool Equals(object obj) => Equals(obj as UniverseTable);

        public override int GetHashCode() =>
            ((Name ?? "").GetHashCode() * 397) ^ (DerivedScript ?? "").GetHashCode();

        public override string ToString() =>
            DerivedScript == null ? Name
            : Name.StartsWith(AliasPrefix, StringComparison.Ordinal) ? Name + " -> " + DerivedScript
            : Name + " [derived]";
    }
}
