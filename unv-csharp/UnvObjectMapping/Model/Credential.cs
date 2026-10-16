namespace UnvObjectMapping.Model
{
    /// <summary>Kullanıcı adı + şifre. ToString şifreyi asla göstermez.</summary>
    public sealed class Credential
    {
        public Credential(string username, string password)
        {
            Username = username;
            Password = password;
        }

        public string Username { get; }
        public string Password { get; }

        public override string ToString() =>
            "kullanici=" + Username + ", sifre=****(" + (Password ?? "").Length + " karakter)";
    }
}
