namespace UnvObjectMapping.Model
{
    /// <summary>Bir UNV nesnesi (dimension, measure, detail).</summary>
    public sealed class UniverseObject
    {
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>Class yolu, ör. "Müşteri\Adres".</summary>
        public string Path { get; set; }

        public string Select { get; set; }
        public string Where { get; set; }

        /// <summary>ACTIVE / HIDDEN (eski uygulamayla aynı).</summary>
        public string State { get; set; }

        /// <summary>Designer'ın Qualification değeri (dimension/measure/detail). Henüz tabloya yazılmıyor.</summary>
        public int? Qualification { get; set; }

        public override string ToString() =>
            "Q" + Qualification + " | " + Path + " | " + Name + " | " + Select;
    }
}
