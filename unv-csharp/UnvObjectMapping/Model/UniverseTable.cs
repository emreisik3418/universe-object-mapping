using System;

namespace UnvObjectMapping.Model
{
    /// <summary>Universe'ün kullandığı tablo (fiziksel, alias çözülmüş veya derived).</summary>
    public sealed class UniverseTable : IEquatable<UniverseTable>
    {
        public UniverseTable(string name, string derivedScript)
        {
            Name = name;
            DerivedScript = derivedScript;
        }

        /// <summary>ŞEMA.TABLO veya Derived.&lt;ad&gt;</summary>
        public string Name { get; }

        /// <summary>Sadece derived table için SQL.</summary>
        public string DerivedScript { get; }

        public bool Equals(UniverseTable other) =>
            other != null && Name == other.Name && DerivedScript == other.DerivedScript;

        public override bool Equals(object obj) => Equals(obj as UniverseTable);

        public override int GetHashCode() =>
            ((Name ?? "").GetHashCode() * 397) ^ (DerivedScript ?? "").GetHashCode();

        public override string ToString() => DerivedScript == null ? Name : Name + " [derived]";
    }
}
