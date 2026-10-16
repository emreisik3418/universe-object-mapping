namespace UnvObjectMapping.Model
{
    /// <summary>CMS'teki bir UNV universe'ün kimlik ve konum bilgisi.</summary>
    public sealed class UniverseInfo
    {
        public int Id { get; set; }
        public string Cuid { get; set; }
        public string Name { get; set; }

        /// <summary>
        /// "Universes" kökü HARİÇ, ters bölü ile ayrılmış klasör yolu. Ör. "AKBANK\AKODE".
        /// Kök klasördeki universe'ler için boş string.
        /// Designer OpenFromEnterprise(FolderPath, Name) bu formatı bekler ve
        /// tablodaki UNIVERSE_PATH kolonuna da eski uygulamayla aynı şekilde bu değer yazılır.
        /// </summary>
        public string FolderPath { get; set; }

        public override string ToString() =>
            (string.IsNullOrEmpty(FolderPath) ? "" : FolderPath + "\\") + Name + " [" + Cuid + "]";
    }
}
