namespace UnvObjectMapping.Model
{
    /// <summary>Sbox'ta tek bir kimlik bilgisi kaydının yeri.</summary>
    public sealed class SboxTarget
    {
        public string AppName { get; set; }
        public string SubAppName { get; set; }
        public string Ip { get; set; }
        public string SensitiveDataName { get; set; }

        public override string ToString() => AppName + "/" + SubAppName + "/" + SensitiveDataName;
    }
}
