namespace UnvObjectMapping.Model
{
    /// <summary>Bir BO ortamı: CMS adresi + kimlik bilgisinin Sbox'taki yeri.</summary>
    public sealed class BoEnvironment
    {
        /// <summary>Ör. "SUNUCU:6400". Logon'da ve tablodaki SYSTEM kolonunda aynı değer kullanılır.</summary>
        public string Cms { get; set; }

        public SboxTarget Sbox { get; set; }

        public override string ToString() => Cms;
    }
}
